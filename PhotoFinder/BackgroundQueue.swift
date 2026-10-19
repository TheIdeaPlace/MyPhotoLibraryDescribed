import Foundation

/// The bookkeeping behind describing in the background: which photos are waiting, how far along it is, and what
/// to say at the end. Apart from the describing itself, so it builds and is tested with any SDK.
struct BackgroundQueue {
    private(set) var pending: [String] = []
    private var queued = Set<String>()
    private(set) var total = 0
    private(set) var done = 0
    private(set) var described = 0
    private(set) var notOnThisMac = 0

    var isEmpty: Bool { pending.isEmpty }

    /// Photos that couldn't be described one after another; reset by one that could.
    private(set) var failedInARow = 0

    /// After this many photos in a row fail, the run stops and says why, rather than failing through thousands (#102),
    /// as on Windows.
    static let failuresInARow = 5

    /// Adds the identifiers not already waiting; returns how many were added. `first` puts them ahead of the rest: a
    /// folder just added goes ahead of the library's backlog.
    @discardableResult
    mutating func add(_ ids: [String], first: Bool = false) -> Int {
        let new = ids.filter { queued.insert($0).inserted }
        if first { pending.insert(contentsOf: new, at: 0) } else { pending.append(contentsOf: new) }
        total += new.count
        return new.count
    }

    /// Puts a photo just taken off the queue back at the front, to try again after a wait (Apple Intelligence was
    /// busy). It isn't counted again.
    mutating func putBack(_ id: String) {
        if queued.insert(id).inserted { pending.insert(id, at: 0) }
    }

    /// A photo couldn't be described; it's left until the next run. True when that's too many in a row and the run
    /// should stop.
    mutating func failed() -> Bool {
        done += 1
        failedInARow += 1
        return failedInARow >= Self.failuresInARow
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
        if wasDescribed {
            described += 1
            failedInARow = 0
        }
    }

    /// The photo is only in iCloud, so it wasn't downloaded to describe it.
    mutating func skippedNotOnThisMac() {
        done += 1
        notOnThisMac += 1
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

    /// What to say when the run stops after too many failures in a row, with `reason`, the last failure.
    static func stoppedMessage(left: Int, reason: String) -> String {
        let notTried = switch left {
        case 0: ""
        case 1: ", so 1 photo wasn't tried"
        default: ", so \(plural(left)) weren't tried"
        }
        return "Background describing stopped: \(plural(failuresInARow)) in a row couldn't be described\(notTried). \(reason)"
    }

    /// What to say when the queue runs out, or nil when there's nothing worth saying.
    var summary: String? {
        guard described > 0 || notOnThisMac > 0 else { return nil }
        // With none described (every photo was only in iCloud), "Finished describing 0 photos" would mislead.
        var text = described > 0 ? "Finished describing \(Self.plural(described)) in the background." : "Background describing finished."
        if notOnThisMac > 0 {
            text += notOnThisMac == 1
                ? " 1 photo wasn't described because it's only in iCloud."
                : " \(Self.plural(notOnThisMac)) weren't described because they're only in iCloud."
        }
        return text
    }

    static func plural(_ count: Int) -> String { count == 1 ? "1 photo" : "\(count.formatted()) photos" }
}
