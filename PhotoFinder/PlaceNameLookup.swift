import Foundation

/// Looks up the names of the places the photos showing were taken (#172), for the list's Place Taken column and the
/// gallery's Place taken label: one at a time, as Apple rate-limits reverse geocoding and asks apps to send far fewer
/// requests than its limit. A short burst (up to `burst` lookups a minute, `burstGap` apart) so the first screen fills
/// quickly, then one every `slowGap`. When Apple says too many, or the network is down, it waits a minute, then two, and
/// so on up to 15, and asks for the same spot again later. Each spot is asked once: found names, and spots with none, are
/// kept in the PlaceNameCache.
///
/// What's wanted changes as you scroll and move: the photo you're on goes first (and the one you left is dropped, so
/// photos passed in the gallery aren't sent later); rows coming into view wait their turn, only the most recent few
/// dozen are kept, and rows that leave the screen are dropped; changing folder or view starts again. Nothing is asked
/// unless `isAllowed` says so (Look Up Place Names is on and Place Taken is showing), checked before every lookup.
@MainActor
final class PlaceNameLookup {
    nonisolated static let burst = 10
    nonisolated static let burstGap: Duration = .milliseconds(1500)
    nonisolated static let slowGap: Duration = .seconds(5)
    nonisolated static let window: Duration = .seconds(60)
    nonisolated static let firstBackOff: Duration = .seconds(60)
    nonisolated static let longestBackOff: Duration = .seconds(15 * 60)
    /// The most rows waiting besides the photo you're on: about a screenful.
    nonisolated static let maxPending = 40

    private let cache: PlaceNameCache
    private let geocode: PlaceGeocoder
    /// Waits between lookups; tests count the waits (and move their clock) instead.
    var sleep: @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }
    /// The time now, for the burst; a test's clock in tests.
    var now: @Sendable () -> ContinuousClock.Instant = { ContinuousClock.now }
    /// Whether an error means Apple is busy or out of reach (asked again later) rather than something about the spot
    /// (not asked again this session).
    var isTransient: @Sendable (Error) -> Bool = PlaceNames.isTransient
    /// Whether anything may be asked now.
    var isAllowed: @MainActor () -> Bool = { false }

    /// The photo you're on, asked first.
    private(set) var current: PlaceKey?
    /// The photo you're on now, even while it's being asked, so one that failed for a moment goes first again only if
    /// you're still on it.
    private var currentPhoto: PlaceKey?
    /// Busy or offline answers for each spot: after `maxTransientFailures`, it isn't asked again this session, so a
    /// spot Apple always fails on can't keep coming back.
    private var transientFailures: [PlaceKey: Int] = [:]
    nonisolated static let maxTransientFailures = 3
    /// Rows that came into view, oldest first.
    private var rowsWanted: [PlaceKey] = []
    private var inFlight: PlaceKey?
    /// Spots Apple couldn't answer for, not asked again until the app starts again.
    private var failed: Set<PlaceKey> = []
    /// When recent lookups were sent, for the burst.
    private var sent: [ContinuousClock.Instant] = []
    /// Busy or offline answers in a row, for backing off.
    private var refusals = 0
    private var worker: Task<Void, Never>?

    init(cache: PlaceNameCache, geocode: @escaping PlaceGeocoder) {
        self.cache = cache
        self.geocode = geocode
    }

    /// What's waiting, in the order it'll be asked.
    var pending: [PlaceKey] {
        var keys = current.map { [$0] } ?? []
        for key in rowsWanted where key != current { keys.append(key) }
        return keys
    }

    /// Asks for a spot's name, unless it's known, being asked or failed already. `first` makes it the photo you're on,
    /// asked next, in place of the one before; otherwise it waits its turn, and only the latest `maxPending` wait.
    func want(_ key: PlaceKey?, first: Bool = false) {
        if first {
            current = nil
            currentPhoto = key
        }
        guard let key, isAllowed(), key != inFlight, !failed.contains(key), cache.name(for: key) == nil else { return }
        if first {
            current = key
        } else {
            rowsWanted.removeAll { $0 == key }
            rowsWanted.append(key)
            if rowsWanted.count > Self.maxPending { rowsWanted.removeFirst(rowsWanted.count - Self.maxPending) }
        }
        if worker == nil { worker = Task { await run() } }
    }

    /// A row left the screen: its spot isn't wanted any more (unless another row showing has it too, and asks again).
    func unwant(_ key: PlaceKey?) {
        guard let key else { return }
        rowsWanted.removeAll { $0 == key }
    }

    /// The folder or view changed, or Place Taken isn't showing: nothing waiting is wanted. A lookup already sent
    /// finishes and is kept.
    func clear() {
        current = nil
        currentPhoto = nil
        rowsWanted.removeAll()
    }

    /// Waits until nothing is waiting or being asked, for tests.
    func whenIdle() async {
        while let worker { await worker.value }
    }

    private func next() -> PlaceKey? {
        if let key = current {
            current = nil
            rowsWanted.removeAll { $0 == key }
            return key
        }
        return rowsWanted.isEmpty ? nil : rowsWanted.removeFirst()
    }

    /// The wait after a lookup: short within the burst, longer after.
    private func gap() -> Duration {
        let time = now()
        sent.removeAll { time - $0 >= Self.window }
        return sent.count < Self.burst ? Self.burstGap : Self.slowGap
    }

    private func run() async {
        defer { worker = nil }
        while let key = next() {
            guard isAllowed() else { clear(); return }
            // Found meanwhile (by Get Info, say).
            guard cache.name(for: key) == nil, !failed.contains(key) else { continue }
            inFlight = key
            sent.append(now())
            let (latitude, longitude) = key.coordinates
            var wait: Duration
            do {
                cache.remember(try await geocode(latitude, longitude), for: key)
                refusals = 0
                wait = gap()
            } catch where isTransient(error) {
                // Busy or offline: wait longer each time, and ask for this spot again later: first if it's still the
                // photo you're on, otherwise after the rest, so one that keeps failing doesn't hold the others up.
                refusals += 1
                wait = min(Self.firstBackOff * (1 << min(refusals - 1, 10)), Self.longestBackOff)
                transientFailures[key, default: 0] += 1
                if transientFailures[key, default: 0] >= Self.maxTransientFailures {
                    failed.insert(key)
                } else if key == currentPhoto, current == nil {
                    current = key
                } else if !rowsWanted.contains(key) {
                    rowsWanted.append(key)
                    if rowsWanted.count > Self.maxPending { rowsWanted.removeFirst(rowsWanted.count - Self.maxPending) }
                }
            } catch {
                failed.insert(key)
                wait = gap()
            }
            inFlight = nil
            // The wait comes after every lookup, even the last, so one wanted just after still waits its turn.
            do { try await sleep(wait) } catch { return }
        }
    }
}
