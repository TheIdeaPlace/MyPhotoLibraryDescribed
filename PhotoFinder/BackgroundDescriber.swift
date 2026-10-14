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
///   Other failures leave the photo out of this session's later runs, and five in a row stop the run, saying why. A
///   refusal by Apple Intelligence's guardrails is a verdict on that photo, not a sign describing doesn't work, so it
///   doesn't count towards the five (#163).
/// - Photos it declines or can't read, and photos that fail while a later one in the same run is described, are also
///   remembered across launches (BackgroundFailures, until the next version), so the same few can't stop every run.
/// - Stopping takes effect at once: the photo in progress is still saved, but nothing more is said.
/// - It speaks only when My Photo Library Described is in front.
@available(macOS 27, *)
@MainActor
final class BackgroundDescriber {
    private let cache: DescriptionCache
    private let progress: @MainActor (String?) -> Void
    private var queue = BackgroundQueue()
    /// Changes when a run stops, so a run still finishing its last photo knows it's been superseded.
    private(set) var generation = 0
    /// Photos that couldn't be described this session, so a new run doesn't try them again.
    private var failedIDs = Set<String>()
    /// The same, across launches (#163), by the photo's description key and date, so an edited photo is tried again.
    private let failures: BackgroundFailures
    /// This run's failures not yet known to be about the photo: remembered once a later photo is described.
    private var failureRun = BackgroundFailures.Run()
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

    init(cache: DescriptionCache, failures: BackgroundFailures, progress: @escaping @MainActor (String?) -> Void) {
        self.cache = cache
        self.failures = failures
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
            .filter { !failures.contains($0.localIdentifier, stamp: BackgroundFailures.stamp(modified: $0.modificationDate)) }
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
        let cache = cache, failures = failures
        let todo = await Task.detached(priority: .utility) {
            paths.filter { path in
                guard !MediaFiles.isVideo((path as NSString).lastPathComponent) else { return false }
                guard let file = FolderScanner.file(at: path) else { return false }
                return cache.description(forKey: file.descriptionKey) == nil && !failures.contains(file.descriptionKey)
            }
        }.value
        add(todo.map(FolderID.file), first: first, announce: text, sayFinish: false, generation: generation)
    }

    /// Queues the photos unless the describer was stopped since `generation` was read, or the setting is off.
    private func add(_ ids: [String], first: Bool, announce text: String?, sayFinish: Bool, generation queuedIn: Int) {
        guard queuedIn == generation, isOn() else { return }
        let added = queue.add(ids.filter { !failedIDs.contains($0) }, first: first)
        guard added > 0 else { return }
        DiagnosticLog.write("background", "Queued \(BackgroundQueue.plural(added))\(first ? " ahead of the rest" : "")")
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
        failureRun.ended()
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
            let failed: Failed?
            let describedBefore = queue.described
            if id.hasPrefix(Self.filePrefix) {
                failed = await describeFile(String(id.dropFirst(Self.filePrefix.count)), run: run)
            } else {
                failed = await describeAsset(id, run: run)
            }
            guard run == generation else { return }
            guard let failed else {
                busyWaits = 0
                // Described (not just skipped): describing works, so this run's earlier failures were about those photos.
                if queue.described > describedBefore { failures.insert(failureRun.described()) }
                continue
            }
            if Self.isBusy(failed.error) {
                // Apple Intelligence limits apps that aren't in front: wait, then try the same photo again.
                queue.putBack(id)
                progress("Background describing waiting for Apple Intelligence")
                if busyWaits == 0 { DiagnosticLog.write("background", "Waiting: Apple Intelligence is rate-limiting") }
                let waits = [30, 60, 120, 300]
                try? await Task.sleep(for: .seconds(waits[min(busyWaits, waits.count - 1)]))
                busyWaits += 1
                continue
            }
            busyWaits = 0
            let failure = failed.error
            failedIDs.insert(id)
            failures.insert(failureRun.failed(BackgroundFailure(key: failed.key, stamp: failed.stamp),
                                              Self.outcome(of: failure)))
            if failure is BackgroundError {
                let name = id.hasPrefix(Self.filePrefix) ? (String(id.dropFirst(Self.filePrefix.count)) as NSString).lastPathComponent
                    : "a photo in the Photos library"
                DiagnosticLog.write("background", "Couldn't read \(name) to describe it")
            }
            if Self.isAboutThisPhoto(failure) {
                queue.finished(described: false)
                continue
            }
            if queue.failed() {
                let left = queue.stop()
                // Five in a row may be the Mac rather than the photos: none of the unproven ones are remembered.
                failureRun.ended()
                generation += 1
                running = false
                progress(nil)
                let reason = failure is BackgroundError ? failure.localizedDescription
                    : "Apple Intelligence couldn't describe them: \(failure.localizedDescription)"
                let message = BackgroundQueue.stoppedMessage(left: left, reason: reason)
                announceFinish = false
                stopped(message)
                DiagnosticLog.write("background", message)
                announce(message)
                return
            }
        }
        guard run == generation else { return }
        DiagnosticLog.write("background", "Run ended: \(queue.described) described, \(queue.notOnThisMac) only in iCloud, \(queue.done) of \(queue.total) done")
        if announceFinish, let summary = queue.summary { announce(summary) }
        announceFinish = false
        queue.reset()
        failureRun.ended()
        running = false
        progress(nil)
    }

    /// Apple Intelligence is busy rather than unable: worth trying the same photo again after a wait.
    private static func isBusy(_ error: Error) -> Bool { PhotoDescriber.isRateLimited(error) }

    /// Whether a failure is remembered at once (about the photo), only once describing is shown to work (anything
    /// else), or not at all (cancelled).
    private static func outcome(of error: Error) -> BackgroundFailures.Outcome {
        if error is CancellationError || Task.isCancelled { return .cancelled }
        if isAboutThisPhoto(error) || (error as? BackgroundError) == .unreadable { return .aboutThePhoto }
        return .other
    }

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

    /// Why a photo couldn't be described, and how BackgroundFailures knows it.
    private struct Failed {
        var error: Error
        var key: String
        var stamp = ""
    }

    /// Describes a photo in the Photos library. Nil when it was described or skipped, or why it couldn't be.
    private func describeAsset(_ id: String, run: Int) async -> Failed? {
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
        let stamp = BackgroundFailures.stamp(modified: asset.modificationDate)
        guard let image else { return Failed(error: BackgroundError.unreadable, key: id, stamp: stamp) }
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
            return Failed(error: error, key: id, stamp: stamp)
        }
    }

    private static let filePrefix = FolderID.file("")

    /// A photo in an added folder: read from disk off the main actor, described, and saved to the description cache
    /// under its file key. Nothing is written into the file here; with Save Descriptions in Photos on, the saved
    /// description goes into a listed file's caption through the usual path. Nil when it was described or skipped.
    private func describeFile(_ path: String, run: Int) async -> Failed? {
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
        // A file that can't be opened (a network share gone, access lapsed) says nothing about the photo; one that
        // opens but isn't a picture does.
        let image = await Task.detached(priority: .utility) { () -> Result<CGImage, BackgroundError> in
            guard let data = try? Data(contentsOf: url, options: .mappedIfSafe) else { return .failure(.notOpened) }
            return ImageConversation.makeImage(from: data).map { .success($0) } ?? .failure(.unreadable)
        }.value
        let key = file.descriptionKey
        let picture: CGImage
        switch image {
        case .success(let image): picture = image
        case .failure(let error): return Failed(error: error, key: key)
        }
        let session = LanguageModelSession(instructions: ImageConversation.instructions)
        let prompt = ImageConversation.describePrompt
        do {
            // Through PhotoDescriber, so Check System's timings and failures count it (#94).
            let answer = try await PhotoDescriber.respond(session, prompt: prompt, image: picture, countsBusy: false)
            if !cache.isEdited(forKey: key) { cache.remember(answer, forKey: key) }
            if run == generation { queue.finished(described: true) }
            return nil
        } catch {
            return Failed(error: error, key: key)
        }
    }

    private enum BackgroundError: LocalizedError {
        /// Read, but not a picture that can be described: about the photo.
        case unreadable
        /// The file couldn't be opened at all: not about the photo, so it's tried again.
        case notOpened
        var errorDescription: String? { "The photos couldn't be read." }
    }

    /// Spoken only when My Photo Library Described is the app in front: new photos usually arrive while it isn't.
    private func announce(_ text: String) {
        guard NSApplication.shared.isActive else { return }
        AccessibilityNotification.Announcement(text).post()
    }
}

#endif

/// Whether a file's contents are on this Mac (#47's review): an iCloud Drive file that's only in iCloud would be
/// downloaded by reading it.
enum FileAvailability {
    /// Asks the file system, not the file: only its attributes are looked at, so nothing is downloaded, and it's quick
    /// enough to ask for every file in a folder (off the main actor).
    @Sendable static func isOnThisMac(_ url: URL) -> Bool {
        guard let values = try? url.resourceValues(forKeys: [.isUbiquitousItemKey, .ubiquitousItemDownloadingStatusKey]),
              values.isUbiquitousItem == true else { return true }
        return values.ubiquitousItemDownloadingStatus == .current
    }

    /// The question before writing into files when some are only in iCloud Drive, as Windows asks about OneDrive's
    /// online-only files (#197): `action` is the command ("Edit Caption"), `files` how many it changes and
    /// `onlyInICloud` how many of those would be downloaded. Nil when none would be, so nothing's asked.
    static func writeQuestion(action: String, files: Int, onlyInICloud: Int) -> String? {
        guard onlyInICloud > 0 else { return nil }
        let changes = files == 1 ? "1 file" : "\(files.formatted()) files"
        let which = files == 1 ? "It's only in iCloud Drive, and will be downloaded to change it."
            : onlyInICloud == files ? "All of them are only in iCloud Drive, and each will be downloaded to change it."
            : onlyInICloud == 1 ? "1 of them is only in iCloud Drive, and will be downloaded to change it."
            : "\(onlyInICloud.formatted()) of them are only in iCloud Drive, and each will be downloaded to change it."
        return "\(action) changes \(changes) on your Mac. \(which) Continue?"
    }
}
