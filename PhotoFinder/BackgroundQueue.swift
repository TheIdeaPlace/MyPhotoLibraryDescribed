import Foundation

/// The bookkeeping behind describing in the background: which photos are waiting, how far along it is, and what
/// to say at the end. Apart from the describing itself, so it builds and is tested with any SDK.
struct BackgroundQueue {
    private(set) var pending: [String] = []
    private var queued = Set<String>()
    private var retries: [String: Int] = [:]
    private(set) var total = 0
    private(set) var done = 0
    private(set) var described = 0
    private(set) var notOnThisMac = 0

    /// How many times a photo that failed is tried again (a rate limit, say) before it's left for later.
    static let maxRetries = 2

    var isEmpty: Bool { pending.isEmpty }

    /// Adds the identifiers not already waiting; returns how many were added.
    @discardableResult
    mutating func add(_ ids: [String]) -> Int {
        let new = ids.filter { queued.insert($0).inserted }
        pending.append(contentsOf: new)
        total += new.count
        return new.count
    }

    /// The next photo to describe, taken off the queue.
    mutating func next() -> String? {
        guard !pending.isEmpty else { return nil }
        let id = pending.removeFirst()
        queued.remove(id)
        return id
    }

    mutating func finished(described wasDescribed: Bool) {
        done += 1
        if wasDescribed { described += 1 }
    }

    /// The photo is only in iCloud, so it wasn't downloaded to describe it.
    mutating func skippedNotOnThisMac() {
        done += 1
        notOnThisMac += 1
    }

    /// Puts a photo that failed back at the end to try again later. False once it's been retried enough.
    mutating func retryLater(_ id: String) -> Bool {
        let count = retries[id, default: 0]
        guard count < Self.maxRetries else {
            done += 1
            return false
        }
        retries[id] = count + 1
        if queued.insert(id).inserted { pending.append(id) }
        return true
    }

    /// Empties the queue and returns how many photos were left.
    mutating func stop() -> Int {
        let left = pending.count
        self = BackgroundQueue()
        return left
    }

    mutating func reset() { self = BackgroundQueue() }

    var progress: String? {
        guard total > 0 else { return nil }
        return "Describing photos in the background, \(min(done + 1, total).formatted()) of \(total.formatted())"
    }

    /// What to say when the queue runs out, or nil when there's nothing worth saying.
    var summary: String? {
        guard described > 0 || notOnThisMac > 0 else { return nil }
        var text = "Finished describing \(Self.plural(described)) in the background."
        if notOnThisMac > 0 {
            text += notOnThisMac == 1
                ? " 1 photo wasn't described because it's only in iCloud."
                : " \(Self.plural(notOnThisMac)) weren't described because they're only in iCloud."
        }
        return text
    }

    static func plural(_ count: Int) -> String { count == 1 ? "1 photo" : "\(count.formatted()) photos" }
}
