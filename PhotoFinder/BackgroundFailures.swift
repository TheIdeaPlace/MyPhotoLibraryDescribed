import Foundation

/// Photos background describing couldn't describe, remembered across launches so a new run doesn't try them again
/// (#163, as Windows' BackgroundFailures.json since #162). Without this, the same few photos at the front of the queue
/// (newest first) failed again at every launch and stopped the run before it reached the rest of the library.
///
/// - Kept per version of the app: an update may describe them, so a different version starts afresh.
/// - Keyed as descriptions are: a Photos library photo by its local identifier, with its modification date as a stamp
///   so an edited photo is tried again; a file by path, size and date (FileDescriptionKey), so an edited file is too.
/// - Only background describing skips them. Describe Photo (Command-D) still describes one on demand, and a
///   description saved for it by any route takes it off the list.
/// - At most `limit` photos, the oldest dropped first, so the file stays small.
///
/// Apart from the describing itself, so it builds and is tested with any SDK.
final class BackgroundFailures: @unchecked Sendable {
    static let fileName = "BackgroundFailures.json"
    static let limit = 2_000

    private struct Entry: Codable, Equatable {
        var key: String
        var stamp: String
    }

    private struct Saved: Codable {
        var version: String
        var photos: [Entry]
    }

    private let fileURL: URL
    private let version: String
    private let limit: Int
    private let lock = NSLock()
    /// Oldest first.
    private var entries: [Entry] = []
    private var observer: NSObjectProtocol?

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
            } else {
                // A new version tries them all again; the old list goes now rather than at the next failure.
                try? FileManager.default.removeItem(at: fileURL)
            }
        }
        if let cache {
            observer = NotificationCenter.default.addObserver(forName: DescriptionCache.didChange, object: cache,
                                                              queue: nil) { [weak self] note in
                guard let key = note.userInfo?[DescriptionCache.keyInfo] as? String else { return }
                self?.remove(key)
            }
        }
    }

    deinit {
        if let observer { NotificationCenter.default.removeObserver(observer) }
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
        return entries.contains(Entry(key: key, stamp: stamp))
    }

    /// Remembers a photo that couldn't be described, and saves the list.
    func insert(_ key: String, stamp: String = "") {
        lock.lock()
        entries.removeAll { $0.key == key }
        entries.append(Entry(key: key, stamp: stamp))
        if entries.count > limit { entries.removeFirst(entries.count - limit) }
        let snapshot = entries
        lock.unlock()
        save(snapshot)
    }

    /// Takes a photo off the list (it's been described), and saves the list if it was on it.
    func remove(_ key: String) {
        lock.lock()
        let before = entries.count
        entries.removeAll { $0.key == key }
        let changed = entries.count != before
        let snapshot = entries
        lock.unlock()
        if changed { save(snapshot) }
    }

    private func save(_ snapshot: [Entry]) {
        guard let data = try? JSONEncoder().encode(Saved(version: version, photos: snapshot)) else { return }
        // Not saved: they're tried again at the next launch; nothing lost but a little time.
        try? FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
    }
}
