import AppKit
import Foundation

/// A photo background describing couldn't describe: its description key (a Photos library photo's local identifier,
/// or a file's FileDescriptionKey) and a stamp (a Photos library photo's modification date, so an edited photo is
/// tried again; a file's key already has its size and date in it).
struct BackgroundFailure: Codable, Hashable, Sendable {
    var key: String
    var stamp: String = ""
}

/// Photos background describing couldn't describe, remembered across launches so a new run doesn't try them again
/// (#163, as Windows' BackgroundFailures.json since #162). Without this, the same few photos at the front of the queue
/// (newest first) failed again at every launch and stopped the run before it reached the rest of the library.
///
/// - Kept per version of the app (any new build): an update may describe them, so a different version starts afresh.
/// - Which failures are remembered is BackgroundFailures.Run's decision: only those that are about the photo.
/// - Only background describing skips them. Describe Photo (Command-D) still describes one on demand, and a
///   description saved for it by any route takes it off the list.
/// - At most `limit` photos, the oldest dropped first, so the file stays small.
///
/// Apart from the describing itself, so it builds and is tested with any SDK.
final class BackgroundFailures: @unchecked Sendable {
    static let fileName = "BackgroundFailures.json"
    static let limit = 2_000

    private struct Saved: Codable {
        var version: String
        var photos: [BackgroundFailure]
    }

    private let fileURL: URL
    private let version: String
    private let limit: Int
    private let lock = NSLock()
    /// Oldest first, for dropping the oldest; `lookup` has the same photos, for `contains`, which is asked for every
    /// photo not described yet as it's queued.
    private var entries: [BackgroundFailure] = []
    private var lookup: Set<BackgroundFailure> = []
    private var saveScheduled = false
    private var observers: [NSObjectProtocol] = []

    /// `cache`, when given, is watched: a description saved for a photo takes it off the list.
    init(fileURL: URL, version: String = AppInfo.version, limit: Int = BackgroundFailures.limit,
         cache: DescriptionCache? = nil) {
        self.fileURL = fileURL
        self.version = version
        self.limit = limit
        if let data = try? Data(contentsOf: fileURL),
           let saved = try? JSONDecoder().decode(Saved.self, from: data) {
            if saved.version == version {
                entries = Array(saved.photos.suffix(limit))
                lookup = Set(entries)
            } else {
                // A new version tries them all again; the old list goes now rather than at the next failure.
                try? FileManager.default.removeItem(at: fileURL)
            }
        }
        if let cache {
            observers.append(NotificationCenter.default.addObserver(forName: DescriptionCache.didChange, object: cache,
                                                                    queue: nil) { [weak self] note in
                guard let key = note.userInfo?[DescriptionCache.keyInfo] as? String else { return }
                self?.remove(key)
            })
        }
        // Saved a couple of seconds after a change; don't lose one made just before quitting.
        observers.append(NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification,
                                                                object: nil, queue: .main) { [weak self] _ in
            self?.save()
        })
    }

    deinit {
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
    }

    /// The file next to the description cache's, in the app's data folder.
    convenience init(beside cache: DescriptionCache) {
        self.init(fileURL: cache.folderURL.appendingPathComponent(Self.fileName), cache: cache)
    }

    /// The stamp for a Photos library photo: its modification date, so an edited photo is tried again.
    static func stamp(modified: Date?) -> String {
        modified.map { String(FileDescriptionKey.ticks($0)) } ?? ""
    }

    var count: Int {
        lock.lock()
        defer { lock.unlock() }
        return entries.count
    }

    /// Whether this photo couldn't be described before, as it is now.
    func contains(_ key: String, stamp: String = "") -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return lookup.contains(BackgroundFailure(key: key, stamp: stamp))
    }

    /// Remembers a photo that couldn't be described; saved shortly.
    func insert(_ key: String, stamp: String = "") { insert([BackgroundFailure(key: key, stamp: stamp)]) }

    func insert(_ failures: [BackgroundFailure]) {
        guard !failures.isEmpty else { return }
        lock.lock()
        let keys = Set(failures.map(\.key))
        entries.removeAll { keys.contains($0.key) }
        entries.append(contentsOf: failures)
        if entries.count > limit { entries.removeFirst(entries.count - limit) }
        lookup = Set(entries)
        lock.unlock()
        scheduleSave()
    }

    /// Takes a photo off the list (it's been described); saved shortly if it was on it.
    func remove(_ key: String) {
        lock.lock()
        guard entries.contains(where: { $0.key == key }) else {
            lock.unlock()
            return
        }
        entries.removeAll { $0.key == key }
        lookup = Set(entries)
        lock.unlock()
        scheduleSave()
    }

    /// A couple of seconds after the last change, not after every one, as DescriptionCache does.
    private func scheduleSave() {
        lock.lock()
        let schedule = !saveScheduled
        saveScheduled = true
        lock.unlock()
        if schedule {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in self?.save() }
        }
    }

    /// Writes the list now. Only called on the main actor (the scheduled save, quitting, tests), so two writes never
    /// overlap.
    func save() {
        lock.lock()
        saveScheduled = false
        let snapshot = entries
        lock.unlock()
        guard let data = try? JSONEncoder().encode(Saved(version: version, photos: snapshot)) else { return }
        // Not saved: they're tried again at the next launch; nothing lost but a little time.
        try? FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
    }

    /// Why a photo couldn't be described, as far as remembering it goes.
    enum Outcome: Equatable, Sendable {
        /// The photo itself: Apple Intelligence declined it (its guardrails), or it couldn't be read.
        case aboutThePhoto
        /// Anything else (a timeout, the model unavailable, the model service failing): may be the Mac, not the photo.
        case other
        /// Stopped or superseded: not a failure at all.
        case cancelled
    }

    /// One run's decisions about which failures to remember (#163's review). A failure that's about the photo is
    /// remembered at once. Any other is only remembered once a later photo in the same run is described, which shows
    /// describing works and so the photo was the problem; if the run stops (five in a row, or Stop Background
    /// Describing) first, they're dropped, so a broken setup doesn't leave good photos skipped until the next update.
    struct Run {
        private(set) var pending: [BackgroundFailure] = []

        /// A photo couldn't be described. Returns what to remember now.
        mutating func failed(_ failure: BackgroundFailure, _ outcome: Outcome) -> [BackgroundFailure] {
            switch outcome {
            case .aboutThePhoto: return [failure]
            case .other:
                pending.append(failure)
                return []
            case .cancelled: return []
            }
        }

        /// A photo was described. Returns the run's earlier failures, now to remember.
        mutating func described() -> [BackgroundFailure] {
            defer { pending = [] }
            return pending
        }

        /// The run stopped or finished: its unproven failures are forgotten.
        mutating func ended() { pending = [] }
    }
}
