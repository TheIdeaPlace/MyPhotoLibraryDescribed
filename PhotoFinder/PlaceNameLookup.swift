import Foundation

/// Looks up the names of the places the photos showing were taken (#172), for the list's Place Taken column and the
/// gallery's Place taken label: one at a time, a gap apart, as Apple throttles reverse geocoding (about 50 a minute; this
/// stays well under). Each spot is asked once: found names, and spots with none, are kept in the PlaceNameCache.
///
/// What's wanted changes as you scroll and move: rows that leave the screen are dropped, the photo you're on goes first,
/// and changing folder or view starts again. Nothing is asked unless `isAllowed` says so (Look Up Place Names is on and
/// Place Taken is showing), checked again before every lookup.
@MainActor
final class PlaceNameLookup {
    /// The gap between lookups: 40 a minute at most.
    nonisolated static let defaultGap: Duration = .milliseconds(1500)
    /// After a lookup fails (offline, or Apple says too many), the wait before the next.
    nonisolated static let defaultFailurePause: Duration = .seconds(60)
    /// The most spots waiting: about a screen or two of rows.
    nonisolated static let maxPending = 80

    private let cache: PlaceNameCache
    private let geocode: PlaceGeocoder
    private let gap: Duration
    private let failurePause: Duration
    /// Waits between lookups; tests count the waits instead.
    var sleep: @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }
    /// Whether anything may be asked now.
    var isAllowed: @MainActor () -> Bool = { false }

    /// Spots waiting, the next first.
    private(set) var pending: [PlaceKey] = []
    private var inFlight: PlaceKey?
    /// Spots whose lookup failed, not asked again until the app starts again, so one Apple won't answer can't be
    /// asked over and over.
    private var failed: Set<PlaceKey> = []
    private var worker: Task<Void, Never>?

    init(cache: PlaceNameCache, geocode: @escaping PlaceGeocoder, gap: Duration = defaultGap,
         failurePause: Duration = defaultFailurePause) {
        self.cache = cache
        self.geocode = geocode
        self.gap = gap
        self.failurePause = failurePause
    }

    /// Asks for a spot's name, unless it's known, waiting, being asked or failed already. `first` puts it ahead of the
    /// rest (the photo you're on); otherwise it waits its turn, and the oldest waiting are dropped past the limit.
    func want(_ key: PlaceKey?, first: Bool = false) {
        guard let key, isAllowed(), key != inFlight, !failed.contains(key), cache.name(for: key) == nil else { return }
        pending.removeAll { $0 == key }
        if first {
            pending.insert(key, at: 0)
        } else {
            pending.append(key)
        }
        if pending.count > Self.maxPending { pending.removeFirst(pending.count - Self.maxPending) }
        if worker == nil { worker = Task { await run() } }
    }

    /// A row left the screen: its spot isn't wanted any more (unless another row showing has it too, and asks again).
    func unwant(_ key: PlaceKey?) {
        guard let key else { return }
        pending.removeAll { $0 == key }
    }

    /// The folder or view changed, or Place Taken isn't showing: nothing waiting is wanted. A lookup already sent
    /// finishes and is kept.
    func clear() {
        pending.removeAll()
    }

    /// Waits until nothing is waiting or being asked, for tests.
    func whenIdle() async {
        while let worker { await worker.value; if self.worker == worker { break } }
    }

    private func run() async {
        defer { worker = nil }
        while !pending.isEmpty {
            guard isAllowed() else { pending.removeAll(); return }
            let key = pending.removeFirst()
            // Found meanwhile (by Get Info, say).
            guard cache.name(for: key) == nil, !failed.contains(key) else { continue }
            inFlight = key
            let (latitude, longitude) = key.coordinates
            var wait = gap
            do {
                cache.remember(try await geocode(latitude, longitude), for: key)
            } catch {
                failed.insert(key)
                wait = failurePause
            }
            inFlight = nil
            // The gap comes after every lookup, even the last, so one wanted just after still waits its turn.
            do { try await sleep(wait) } catch { return }
        }
    }
}
