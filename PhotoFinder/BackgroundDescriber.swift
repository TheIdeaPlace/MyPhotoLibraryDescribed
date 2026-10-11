import FoundationModels
import ImageIO
import Photos
import SwiftUI

#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older

/// Describes photos added to the Photos library behind the scenes (a setting, off until turned on), with
/// Apple Intelligence, so their descriptions are ready in Get Info without waiting.
///
/// One photo at a time, into the same cache Describe Image uses. Photos are queued by identifier and fetched again
/// when their turn comes, so one deleted meanwhile is skipped. Photos only in iCloud are skipped rather than
/// downloaded. Progress is in the status bar, and finishing is announced. It stops when the app quits; photos it
/// hadn't reached are described when you press Describe Image, as usual.
@available(macOS 27, *)
@MainActor
final class BackgroundDescriber {
    private let cache: DescriptionCache
    private let progress: (String?) -> Void
    private var queue: [String] = []
    private var queued = Set<String>()
    private var task: Task<Void, Never>?
    private var total = 0
    private var done = 0

    init(cache: DescriptionCache, progress: @escaping (String?) -> Void) {
        self.cache = cache
        self.progress = progress
    }

    var isRunning: Bool { task != nil }

    /// Queues the photos (not videos) that haven't been described yet.
    func enqueue(_ assets: [PHAsset]) {
        guard ImageConversation.unavailableReason == nil else { return }
        let new = assets.filter {
            $0.mediaType == .image && !queued.contains($0.localIdentifier) && cache.description(for: $0) == nil
        }
        guard !new.isEmpty else { return }
        for asset in new {
            queue.append(asset.localIdentifier)
            queued.insert(asset.localIdentifier)
        }
        total += new.count
        announce("Describing \(Self.plural(new.count)) in the background")
        showProgress()
        if task == nil {
            task = Task { await run() }
        }
    }

    /// Stops this run (the photo being described finishes) and returns how many were left.
    @discardableResult
    func stop() -> Int {
        let left = queue.count
        queue.removeAll()
        queued.removeAll()
        if task == nil { finish() }
        return left
    }

    private func run() async {
        var described = 0
        while !queue.isEmpty, !Task.isCancelled {
            let id = queue.removeFirst()
            queued.remove(id)
            showProgress()
            defer { done += 1 }
            guard let asset = PHAsset.fetchAssets(withLocalIdentifiers: [id], options: nil).firstObject,
                  cache.description(for: asset) == nil,
                  ImageConversation.unavailableReason == nil,
                  let data = await Self.imageData(for: asset),
                  let image = ImageConversation.makeImage(from: data) else { continue }
            let session = LanguageModelSession(instructions: ImageConversation.instructions)
            let prompt = ImageConversation.describePrompt
            if let answer = try? await session.respond({
                prompt
                Attachment(image)
            }).content {
                cache.remember(answer, for: asset)
                described += 1
            }
        }
        task = nil
        if described > 0 { announce("Finished describing \(Self.plural(described)) in the background") }
        finish()
    }

    private func showProgress() {
        progress("Describing photos in the background, \(min(done + 1, max(total, 1))) of \(total)")
    }

    private func finish() {
        total = 0
        done = 0
        progress(nil)
    }

    private func announce(_ text: String) {
        AccessibilityNotification.Announcement(text).post()
    }

    private static func plural(_ count: Int) -> String { count == 1 ? "1 photo" : "\(count) photos" }

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
