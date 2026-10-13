import AppKit
import FoundationModels
import ImageIO
import Photos
import SwiftUI

#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older

/// Describes photos added to the Photos library behind the scenes (a setting, off until turned on), with
/// Apple Intelligence, so their descriptions are ready in Get Info without waiting.
///
/// - One photo at a time, into the same cache Describe Image uses. The bookkeeping is in BackgroundQueue.
/// - Photos are queued by identifier and fetched again when their turn comes, so one deleted meanwhile is skipped.
/// - Photos only in iCloud aren't downloaded for this; the summary says how many were skipped.
/// - A photo that fails (Apple Intelligence rate-limits apps that aren't in front) is tried again later.
/// - Stopping takes effect at once: the photo in progress is still saved, but nothing more is said.
/// - It speaks only when Photo Finder is in front, and only at the start and end of a run.
@available(macOS 27, *)
@MainActor
final class BackgroundDescriber {
    private let cache: DescriptionCache
    private let progress: @MainActor (String?) -> Void
    private var queue = BackgroundQueue()
    /// Changes when a run stops, so a run still finishing its last photo knows it's been superseded.
    private var generation = 0
    private var running = false
    /// Whether a file queued earlier should still be described: false once its folder leaves the library.
    var isWanted: @MainActor (String) -> Bool = { _ in true }
    /// Whether the photo you're on is being described right now (automatic describing, describing ahead, Describe
    /// Photo): background work waits for it, so the photo you're on always comes first.
    var isForegroundBusy: @MainActor () -> Bool = { false }

    init(cache: DescriptionCache, progress: @escaping @MainActor (String?) -> Void) {
        self.cache = cache
        self.progress = progress
    }

    /// Queues the photos (not videos) that haven't been described yet.
    func enqueue(_ assets: [PHAsset]) {
        guard ImageConversation.unavailableReason == nil else { return }
        let ids = assets
            .filter { $0.mediaType == .image && cache.description(for: $0) == nil }
            .map(\.localIdentifier)
        add(ids)
    }

    /// Queues photos in added folders (#47), by path. Each is looked up again when its turn comes, so a file deleted
    /// or already described meanwhile is skipped and an edited one is described under its current key.
    func enqueue(filePaths paths: [String]) {
        guard ImageConversation.unavailableReason == nil else { return }
        add(paths.filter { !MediaFiles.isVideo(($0 as NSString).lastPathComponent) }.map(FolderID.file))
    }

    private func add(_ ids: [String]) {
        guard queue.add(ids) > 0 else { return }
        progress(queue.progress)
        guard !running else { return }
        running = true
        announce("Describing new photos in the background")
        let run = generation
        Task { await self.run(run) }
    }

    /// Stops this run and returns how many photos were left.
    @discardableResult
    func stop() -> Int {
        let left = queue.stop()
        generation += 1
        running = false
        progress(nil)
        return left
    }

    private func run(_ run: Int) async {
        while run == generation, let id = queue.next() {
            progress(queue.progress)
            // The photo you're on first: wait while it's being described, up to a minute at a time.
            for _ in 0..<600 where isForegroundBusy() && run == generation {
                try? await Task.sleep(for: .milliseconds(100))
            }
            guard run == generation else { return }
            if id.hasPrefix(Self.filePrefix) {
                await describeFile(String(id.dropFirst(Self.filePrefix.count)), id: id, run: run)
                continue
            }
            guard let asset = PHAsset.fetchAssets(withLocalIdentifiers: [id], options: nil).firstObject,
                  cache.description(for: asset) == nil,
                  ImageConversation.unavailableReason == nil else {
                queue.finished(described: false)
                continue
            }
            // Photos only in iCloud aren't downloaded for this.
            guard let data = await PhotoData.request(asset, allowNetwork: false)?.data else {
                queue.skippedNotOnThisMac()
                continue
            }
            // Decoded off the main actor: a 2048-pixel decode for every photo would stall the window.
            let image = await Task.detached(priority: .utility) { ImageConversation.makeImage(from: data) }.value
            guard let image else {
                queue.finished(described: false)
                continue
            }
            let session = LanguageModelSession(instructions: ImageConversation.instructions)
            let prompt = ImageConversation.describePrompt
            do {
                let answer = try await PhotoDescriber.respond(session, prompt: prompt, image: image)
                // Corrected in the Description panel while this was being described: the person's words win.
                if !cache.isEdited(forKey: asset.localIdentifier) { cache.remember(answer, for: asset) }
                guard run == generation else { return }
                queue.finished(described: true)
            } catch {
                guard run == generation else { return }
                // Often a rate limit while Photo Finder isn't in front: wait, then try it again later.
                if queue.retryLater(id) {
                    try? await Task.sleep(for: .seconds(15))
                }
            }
        }
        guard run == generation else { return }
        if let summary = queue.summary { announce(summary) }
        queue.reset()
        running = false
        progress(nil)
    }

    private static let filePrefix = FolderID.file("")

    /// A photo in an added folder: read from disk off the main actor, described, and saved to the description cache
    /// under its file key. Nothing is written into the file here; with Save Descriptions in Photos on, the saved
    /// description goes into a listed file's caption through the usual path.
    private func describeFile(_ path: String, id: String, run: Int) async {
        guard isWanted(path) else {
            queue.finished(described: false)
            return
        }
        let file = await Task.detached(priority: .utility) { FolderScanner.file(at: path) }.value
        guard let file, !file.isVideo, cache.description(forKey: file.descriptionKey) == nil,
              ImageConversation.unavailableReason == nil else {
            queue.finished(described: false)
            return
        }
        // Only in iCloud Drive (not downloaded): reading it would download it, so it's skipped, as Photos' iCloud-only
        // photos are.
        let url0 = file.url
        guard await Task.detached(priority: .utility, operation: { FileAvailability.isOnThisMac(url0) }).value else {
            queue.skippedNotOnThisMac()
            return
        }
        let url = file.url
        let image = await Task.detached(priority: .utility) { () -> CGImage? in
            guard let data = try? Data(contentsOf: url, options: .mappedIfSafe) else { return nil }
            return ImageConversation.makeImage(from: data)
        }.value
        guard let image else {
            queue.finished(described: false)
            return
        }
        let session = LanguageModelSession(instructions: ImageConversation.instructions)
        let prompt = ImageConversation.describePrompt
        do {
            let answer = try await PhotoDescriber.respond(session, prompt: prompt, image: image)
            let key = file.descriptionKey
            if !cache.isEdited(forKey: key) { cache.remember(answer, forKey: key) }
            guard run == generation else { return }
            queue.finished(described: true)
        } catch {
            guard run == generation else { return }
            if queue.retryLater(id) {
                try? await Task.sleep(for: .seconds(15))
            }
        }
    }

    /// Spoken only when Photo Finder is the app in front: new photos usually arrive while it isn't.
    private func announce(_ text: String) {
        guard NSApplication.shared.isActive else { return }
        AccessibilityNotification.Announcement(text).post()
    }
}

#endif

/// Whether a file's contents are on this Mac (#47's review): an iCloud Drive file that's only in iCloud would be
/// downloaded by reading it.
enum FileAvailability {
    static func isOnThisMac(_ url: URL) -> Bool {
        guard let values = try? url.resourceValues(forKeys: [.isUbiquitousItemKey, .ubiquitousItemDownloadingStatusKey]),
              values.isUbiquitousItem == true else { return true }
        return values.ubiquitousItemDownloadingStatus == .current
    }
}
