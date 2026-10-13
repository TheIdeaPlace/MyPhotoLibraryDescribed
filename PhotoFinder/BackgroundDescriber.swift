import AppKit
import FoundationModels
import ImageIO
import Photos
import SwiftUI

#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older

/// Photo > Describe Photos in the Background (#102): describes every photo not described yet, behind the scenes, with
/// Apple Intelligence, so descriptions are ready when you get to them. As on Windows (#96, #97):
///
/// - The library's photos newest first, when it's turned on and at each launch, so a big library carries on across
///   restarts; a folder just added goes ahead of that backlog, and photos added later are described as they come.
/// - One photo at a time, into the same cache Describe Photo uses. The bookkeeping is in BackgroundQueue.
/// - Photos are queued by identifier and fetched again when their turn comes, so one deleted meanwhile is skipped.
/// - Photos only in iCloud aren't downloaded for this; the summary says how many were skipped.
/// - The photo you're on always comes first; it waits on Low Power Mode and while the Mac is running hot.
/// - When Apple Intelligence is busy (it limits apps that aren't in front), it waits and tries the same photo again.
///   Other failures leave the photo for the next run, and five in a row stop the run, saying why.
/// - Stopping takes effect at once: the photo in progress is still saved, but nothing more is said.
/// - It speaks only when Photo Finder is in front.
@available(macOS 27, *)
@MainActor
final class BackgroundDescriber {
    private let cache: DescriptionCache
    private let progress: @MainActor (String?) -> Void
    private var queue = BackgroundQueue()
    /// Changes when a run stops, so a run still finishing its last photo knows it's been superseded.
    private(set) var generation = 0
    /// Photos that couldn't be described this session, so a new run doesn't try them again; the next launch does.
    private var failedIDs = Set<String>()
    /// Whether this run was started by something the person did, so its finish is said (as on Windows).
    private var announceFinish = false
    private var running = false
    /// Whether a file queued earlier should still be described: false once its folder leaves the library.
    var isWanted: @MainActor (String) -> Bool = { _ in true }
    /// Whether the photo you're on is being described right now (automatic describing, describing ahead, Describe
    /// Photo): background work waits for it, so the photo you're on always comes first.
    var isForegroundBusy: @MainActor () -> Bool = { false }
    /// Why background work should wait now (Low Power Mode, running hot), or nil when it can go on.
    var pauseReason: @MainActor () -> String? = {
        if ProcessInfo.processInfo.isLowPowerModeEnabled { return "Background describing paused on Low Power Mode" }
        if ProcessInfo.processInfo.thermalState.rawValue >= ProcessInfo.ThermalState.serious.rawValue {
            return "Background describing paused while the Mac is hot"
        }
        return nil
    }
    /// Whether Describe Photos in the Background is on.
    var isOn: @MainActor () -> Bool = { true }
    /// The run stopped after too many failures in a row; says why, for the status bar.
    var stopped: @MainActor (String) -> Void = { _ in }

    init(cache: DescriptionCache, progress: @escaping @MainActor (String?) -> Void) {
        self.cache = cache
        self.progress = progress
    }

    /// Queues the photos (not videos) that haven't been described yet. `first` puts them ahead of the backlog.
    /// `announce`, when given, is said as they're queued; `sayFinish` says when the run is done. `since` is the
    /// generation the work began in: stopped since then, they're dropped.
    func enqueue(_ assets: [PHAsset], first: Bool = false, announce text: String? = nil, sayFinish: Bool = false,
                 since: Int? = nil) {
        guard ImageConversation.unavailableReason == nil else { return }
        let ids = assets
            .filter { $0.mediaType == .image && cache.description(for: $0) == nil }
            .map(\.localIdentifier)
        add(ids, first: first, announce: text, sayFinish: sayFinish, generation: since ?? generation)
    }

    /// Queues photos in added folders (#47), by path, leaving out those already described. Each is looked up again
    /// when its turn comes, so a file deleted or described meanwhile is skipped and an edited one is described under
    /// its current key.
    func enqueue(filePaths paths: [String], first: Bool = false, announce text: String? = nil,
                 since: Int? = nil) async {
        guard ImageConversation.unavailableReason == nil else { return }
        // Stopped, or turned off, while the files were being looked at: they're dropped.
        let generation = since ?? generation
        let cache = cache
        let todo = await Task.detached(priority: .utility) {
            paths.filter { path in
                guard !MediaFiles.isVideo((path as NSString).lastPathComponent) else { return false }
                guard let file = FolderScanner.file(at: path) else { return false }
                return cache.description(forKey: file.descriptionKey) == nil
            }
        }.value
        add(todo.map(FolderID.file), first: first, announce: text, sayFinish: false, generation: generation)
    }

    /// Queues the photos unless the describer was stopped since `generation` was read, or the setting is off.
    private func add(_ ids: [String], first: Bool, announce text: String?, sayFinish: Bool, generation queuedIn: Int) {
        guard queuedIn == generation, isOn() else { return }
        let added = queue.add(ids.filter { !failedIDs.contains($0) }, first: first)
        guard added > 0 else { return }
        if sayFinish { announceFinish = true }
        if let text {
            announce(text.replacingOccurrences(of: "{count}", with: BackgroundQueue.plural(added)))
            announceFinish = true
        }
        progress(queue.progress)
        guard !running else { return }
        running = true
        let run = generation
        Task { await self.run(run) }
    }

    /// Stops this run and returns how many photos were left.
    @discardableResult
    func stop() -> Int {
        let left = queue.stop()
        generation += 1
        running = false
        announceFinish = false
        progress(nil)
        return left
    }

    private func run(_ run: Int) async {
        // Apple Intelligence busy several times running: wait longer each time, up to five minutes.
        var busyWaits = 0
        while run == generation, let id = queue.next() {
            progress(queue.progress)
            // The photo you're on first: wait while it's being described, up to a minute at a time.
            for _ in 0..<600 where isForegroundBusy() && run == generation {
                try? await Task.sleep(for: .milliseconds(100))
            }
            // Low Power Mode or running hot: wait until that's over.
            while run == generation, let reason = pauseReason() {
                progress(reason)
                try? await Task.sleep(for: .seconds(10))
            }
            guard run == generation else { return }
            progress(queue.progress)
            let failure: Error?
            if id.hasPrefix(Self.filePrefix) {
                failure = await describeFile(String(id.dropFirst(Self.filePrefix.count)), run: run)
            } else {
                failure = await describeAsset(id, run: run)
            }
            guard run == generation else { return }
            guard let failure else {
                busyWaits = 0
                continue
            }
            if Self.isBusy(failure) {
                // Apple Intelligence limits apps that aren't in front: wait, then try the same photo again.
                queue.putBack(id)
                progress("Background describing waiting for Apple Intelligence")
                let waits = [30, 60, 120, 300]
                try? await Task.sleep(for: .seconds(waits[min(busyWaits, waits.count - 1)]))
                busyWaits += 1
                continue
            }
            busyWaits = 0
            failedIDs.insert(id)
            if Self.isAboutThisPhoto(failure) {
                queue.finished(described: false)
                continue
            }
            if queue.failed() {
                let left = queue.stop()
                generation += 1
                running = false
                progress(nil)
                let reason = failure is BackgroundError ? failure.localizedDescription
                    : "Apple Intelligence couldn't describe them: \(failure.localizedDescription)"
                let message = BackgroundQueue.stoppedMessage(left: left, reason: reason)
                announceFinish = false
                stopped(message)
                announce(message)
                return
            }
        }
        guard run == generation else { return }
        if announceFinish, let summary = queue.summary { announce(summary) }
        announceFinish = false
        queue.reset()
        running = false
        progress(nil)
    }

    /// Apple Intelligence is busy rather than unable: worth trying the same photo again after a wait.
    private static func isBusy(_ error: Error) -> Bool { PhotoDescriber.isRateLimited(error) }

    /// Apple Intelligence wouldn't describe this one photo (its content), which says nothing about the next.
    private static func isAboutThisPhoto(_ error: Error) -> Bool {
        if let error = error as? LanguageModelError {
            switch error {
            case .guardrailViolation, .refusal: return true
            default: return false
            }
        }
        if let error = error as? LanguageModelSession.GenerationError {
            switch error {
            case .guardrailViolation, .refusal: return true
            default: return false
            }
        }
        return false
    }

    /// Describes a photo in the Photos library. Nil when it was described or skipped, or why it couldn't be.
    private func describeAsset(_ id: String, run: Int) async -> Error? {
        guard let asset = PHAsset.fetchAssets(withLocalIdentifiers: [id], options: nil).firstObject,
              cache.description(for: asset) == nil,
              ImageConversation.unavailableReason == nil else {
            queue.finished(described: false)
            return nil
        }
        // Photos only in iCloud aren't downloaded for this.
        guard let data = await PhotoData.request(asset, allowNetwork: false)?.data else {
            if run == generation { queue.skippedNotOnThisMac() }
            return nil
        }
        // Decoded off the main actor: a 2048-pixel decode for every photo would stall the window.
        let image = await Task.detached(priority: .utility) { ImageConversation.makeImage(from: data) }.value
        guard let image else { return BackgroundError.unreadable }
        let session = LanguageModelSession(instructions: ImageConversation.instructions)
        let prompt = ImageConversation.describePrompt
        do {
            // Through PhotoDescriber, so Check System's timings and failures count it (#94).
            let answer = try await PhotoDescriber.respond(session, prompt: prompt, image: image, countsBusy: false)
            // Corrected in the Description panel while this was being described: the person's words win.
            if !cache.isEdited(forKey: asset.localIdentifier) { cache.remember(answer, for: asset) }
            if run == generation { queue.finished(described: true) }
            return nil
        } catch {
            return error
        }
    }

    private static let filePrefix = FolderID.file("")

    /// A photo in an added folder: read from disk off the main actor, described, and saved to the description cache
    /// under its file key. Nothing is written into the file here; with Save Descriptions in Photos on, the saved
    /// description goes into a listed file's caption through the usual path. Nil when it was described or skipped.
    private func describeFile(_ path: String, run: Int) async -> Error? {
        guard isWanted(path) else {
            queue.finished(described: false)
            return nil
        }
        let file = await Task.detached(priority: .utility) { FolderScanner.file(at: path) }.value
        guard run == generation else { return nil }
        guard let file, !file.isVideo, cache.description(forKey: file.descriptionKey) == nil,
              ImageConversation.unavailableReason == nil else {
            queue.finished(described: false)
            return nil
        }
        // Only in iCloud Drive (not downloaded): reading it would download it, so it's skipped, as Photos' iCloud-only
        // photos are.
        let url = file.url
        guard await Task.detached(priority: .utility, operation: { FileAvailability.isOnThisMac(url) }).value else {
            if run == generation { queue.skippedNotOnThisMac() }
            return nil
        }
        let image = await Task.detached(priority: .utility) { () -> CGImage? in
            guard let data = try? Data(contentsOf: url, options: .mappedIfSafe) else { return nil }
            return ImageConversation.makeImage(from: data)
        }.value
        guard let image else { return BackgroundError.unreadable }
        let session = LanguageModelSession(instructions: ImageConversation.instructions)
        let prompt = ImageConversation.describePrompt
        do {
            // Through PhotoDescriber, so Check System's timings and failures count it (#94).
            let answer = try await PhotoDescriber.respond(session, prompt: prompt, image: image, countsBusy: false)
            let key = file.descriptionKey
            if !cache.isEdited(forKey: key) { cache.remember(answer, forKey: key) }
            if run == generation { queue.finished(described: true) }
            return nil
        } catch {
            return error
        }
    }

    private enum BackgroundError: LocalizedError {
        case unreadable
        var errorDescription: String? { "The photos couldn't be read." }
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
