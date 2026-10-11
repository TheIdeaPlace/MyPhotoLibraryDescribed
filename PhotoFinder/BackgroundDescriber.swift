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
            guard let asset = PHAsset.fetchAssets(withLocalIdentifiers: [id], options: nil).firstObject,
                  cache.description(for: asset) == nil,
                  ImageConversation.unavailableReason == nil else {
                queue.finished(described: false)
                continue
            }
            guard let data = await Self.imageData(for: asset) else {
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
                let answer = try await session.respond {
                    prompt
                    Attachment(image)
                }.content
                cache.remember(answer, for: asset)
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

    /// Spoken only when Photo Finder is the app in front: new photos usually arrive while it isn't.
    private func announce(_ text: String) {
        guard NSApplication.shared.isActive else { return }
        AccessibilityNotification.Announcement(text).post()
    }

    /// The photo's data if it's on this Mac; photos only in iCloud aren't downloaded for this.
    private static func imageData(for asset: PHAsset) async -> Data? {
        let options = PHImageRequestOptions()
        options.isNetworkAccessAllowed = false
        options.version = .current
        options.deliveryMode = .highQualityFormat
        let box = UncheckedBox(value: asset)
        return await withCheckedContinuation { continuation in
            PHImageManager.default().requestImageDataAndOrientation(for: box.value, options: options) { data, _, _, _ in
                continuation.resume(returning: data)
            }
        }
    }
}

#endif
