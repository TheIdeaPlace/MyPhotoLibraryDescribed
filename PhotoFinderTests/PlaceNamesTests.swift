import XCTest
@testable import Photo_Finder

final class PlaceNamesTests: XCTestCase {
    func testOffUntilTurnedOn() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "PlaceNamesTests"))
        defaults.removePersistentDomain(forName: "PlaceNamesTests")
        defer { defaults.removePersistentDomain(forName: "PlaceNamesTests") }
        XCTAssertFalse(PlaceNames.isOn(in: defaults))
        defaults.set(true, forKey: PlaceNames.key)
        XCTAssertTrue(PlaceNames.isOn(in: defaults))
        defaults.set(false, forKey: PlaceNames.key)
        XCTAssertFalse(PlaceNames.isOn(in: defaults))
    }

    func testTheQuestionSaysWhatIsSentAndToWhom() {
        XCTAssertTrue(PlaceNames.explanation.contains("coordinates"))
        XCTAssertTrue(PlaceNames.explanation.contains("Apple"))
        XCTAssertTrue(PlaceNames.explanation.contains("turn it off"))
    }

    func testAPlaceIsNamedWithoutRepeatsOrGaps() {
        XCTAssertEqual(PlaceNames.joined(["Golden Gate Park", "San Francisco", "CA", "United States"]),
                       "Golden Gate Park, San Francisco, CA, United States")
        XCTAssertEqual(PlaceNames.joined(["Cupertino", "Cupertino", "CA", "United States"]), "Cupertino, CA, United States")
        XCTAssertEqual(PlaceNames.joined([nil, "Paris", " ", "France"]), "Paris, France")
        XCTAssertNil(PlaceNames.joined([nil, ""]))
    }

    func testTheQuestionSaysTheColumnAndLabelSendCoordinatesToo() {
        XCTAssertTrue(PlaceNames.explanation.contains("Get Info"))
        XCTAssertTrue(PlaceNames.explanation.contains("Place Taken"))
        XCTAssertTrue(PlaceNames.explanation.contains("kept on your Mac"))
    }

    // MARK: - Keys (#172)

    func testASpotIsKeyedToAboutAMetreAsOnWindows() throws {
        let key = try XCTUnwrap(PlaceKey(latitude: 47.609704, longitude: -122.342196))
        XCTAssertEqual(key.text, "47.60970,-122.34220")
        XCTAssertEqual(PlaceKey(latitude: 47.6097041, longitude: -122.3421999), key, "within a metre is the same spot")
        XCTAssertNotEqual(PlaceKey(latitude: 47.60972, longitude: -122.342196), key)
        XCTAssertEqual(PlaceKey("47.60970,-122.34220"), key)
        XCTAssertEqual(PlaceKey(latitude: -0.5, longitude: 0.000001)?.text, "-0.50000,0.00000")
        XCTAssertEqual(PlaceKey(latitude: 0, longitude: 180)?.text, "0.00000,180.00000")
        XCTAssertEqual(PlaceKey(latitude: -33.000014, longitude: 151.2)?.text, "-33.00001,151.20000")
        XCTAssertEqual(key.coordinates.latitude, 47.6097, accuracy: 0.000001)
        XCTAssertEqual(key.coordinates.longitude, -122.3422, accuracy: 0.000001)
    }

    func testCoordinatesOffTheEarthHaveNoKey() {
        XCTAssertNil(PlaceKey(latitude: 91, longitude: 0))
        XCTAssertNil(PlaceKey(latitude: 0, longitude: -181))
        XCTAssertNil(PlaceKey(latitude: .nan, longitude: 0))
        XCTAssertNil(PlaceKey(latitude: 0, longitude: .infinity))
        XCTAssertNil(PlaceKey("not a place"))
        XCTAssertNil(PlaceKey("1,2,3"))
        XCTAssertNil(PlaceKey("95,2"))
    }

    // MARK: - The cache (#172)

    private func cacheURL() -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("PlaceNamesTests-\(UUID().uuidString)")
        addTeardownBlock { try? FileManager.default.removeItem(at: folder) }
        return folder.appendingPathComponent("PlaceNames.json")
    }

    func testNamesAreKeptBetweenLaunchesAsWindowsKeepsThem() throws {
        let url = cacheURL()
        let key = try XCTUnwrap(PlaceKey(latitude: 47.6097, longitude: -122.3422))
        let cache = PlaceNameCache(fileURL: url)
        XCTAssertNil(cache.name(for: key))
        cache.remember("Pike Place Market, Seattle, WA, United States", for: key)

        let saved = try JSONDecoder().decode([String: String].self, from: Data(contentsOf: url))
        XCTAssertEqual(saved, ["47.60970,-122.34220": "Pike Place Market, Seattle, WA, United States"])
        XCTAssertEqual(PlaceNameCache(fileURL: url).name(for: key), "Pike Place Market, Seattle, WA, United States")
    }

    func testASpotWithNoNameIsRememberedSoItIsntAskedAgain() async throws {
        let url = cacheURL()
        let key = try XCTUnwrap(PlaceKey(latitude: 0, longitude: -140))
        let asked = Counter()
        let cache = PlaceNameCache(fileURL: url)
        let first = await cache.lookUp(key) { _, _ in asked.add(); return nil }
        XCTAssertNil(first)
        XCTAssertEqual(cache.name(for: key), "")
        let again = await PlaceNameCache(fileURL: url).lookUp(key) { _, _ in asked.add(); return "Somewhere" }
        XCTAssertNil(again)
        XCTAssertEqual(asked.count, 1)
    }

    func testAFailedLookupIsntRemembered() async throws {
        let url = cacheURL()
        let key = try XCTUnwrap(PlaceKey(latitude: 51.5, longitude: -0.12))
        let cache = PlaceNameCache(fileURL: url)
        let failed = await cache.lookUp(key) { _, _ in throw URLError(.notConnectedToInternet) }
        XCTAssertNil(failed)
        XCTAssertNil(cache.name(for: key))
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
        let found = await cache.lookUp(key) { latitude, longitude in
            XCTAssertEqual(latitude, 51.5, accuracy: 0.000001)
            XCTAssertEqual(longitude, -0.12, accuracy: 0.000001)
            return "London, England, United Kingdom"
        }
        XCTAssertEqual(found, "London, England, United Kingdom")
    }

    func testAnUnreadableFileStartsEmptyAndIsWrittenAfresh() throws {
        let url = cacheURL()
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("not json".utf8).write(to: url)
        let key = try XCTUnwrap(PlaceKey(latitude: 1, longitude: 2))
        let cache = PlaceNameCache(fileURL: url)
        XCTAssertNil(cache.name(for: key))
        cache.remember("Here", for: key)
        XCTAssertEqual(PlaceNameCache(fileURL: url).name(for: key), "Here")

        // Keys that aren't spots are left out; the rest are read.
        try Data(#"{"nonsense": "x", "1.00000,2.00000": "Here", "3.00000,4.00000": ""}"#.utf8).write(to: url)
        let reread = PlaceNameCache(fileURL: url)
        XCTAssertEqual(reread.snapshot().count, 2)
        XCTAssertEqual(reread.name(for: try XCTUnwrap(PlaceKey(latitude: 3, longitude: 4))), "")
    }

    // MARK: - What Place Taken shows (#172)

    private func row(latitude: Double? = 47.6097, longitude: Double? = -122.3422) -> FileRow {
        var metadata = FileMetadata()
        metadata.latitude = latitude
        metadata.longitude = longitude
        let file = MediaFile(path: "/p/IMG_1.HEIC", size: 1, created: nil, modified: Date(timeIntervalSince1970: 0))
        return FileRow(file: file, metadata: metadata)
    }

    func testPlaceTakenShowsTheNameWhenKnownAndTheCoordinatesOtherwise() {
        let plain = row()
        XCTAssertEqual(plain.placeKey?.text, "47.60970,-122.34220")
        XCTAssertEqual(plain.text(for: "location"), "47.6097, -122.3422")
        XCTAssertEqual(plain.line(for: ["location"]), "Place taken 47.6097, -122.3422")

        let named = plain.withPlaceName("Pike Place Market, Seattle")
        XCTAssertEqual(named.text(for: "location"), "Pike Place Market, Seattle")
        XCTAssertEqual(named.line(for: ["location"]), "Place taken Pike Place Market, Seattle")
        XCTAssertEqual(GalleryLabels.text(for: named, position: (1, 2), items: [.location]), "Place taken Pike Place Market, Seattle")
        XCTAssertEqual(GalleryLabels.text(for: plain, position: (1, 2), items: [.location]), "Place taken 47.6097, -122.3422")

        let nowhere = row(latitude: nil, longitude: nil)
        XCTAssertNil(nowhere.placeKey)
        XCTAssertEqual(nowhere.text(for: "location"), "")
    }

    func testPlaceTakenSortsByWhatItShows() {
        let named = row().withPlaceName("Zurich")
        let other = row(latitude: 10, longitude: 10)
        let sorted = ColumnOrder.sorted([named, other], by: [ColumnOrder.comparator("location")])
        XCTAssertEqual(sorted.map { $0.text(for: "location") }, ["10.0000, 10.0000", "Zurich"])
        let third = row(latitude: 1, longitude: 1).withPlaceName("Aarau")
        let all = ColumnOrder.sorted([named, other, third], by: [ColumnOrder.comparator("location")])
        XCTAssertEqual(all.map { $0.text(for: "location") }, ["10.0000, 10.0000", "Aarau", "Zurich"],
                       "by the name shown, not the coordinates behind it")
        XCTAssertEqual(ColumnOrder.sortColumn(of: [ColumnOrder.comparator("location")]), "location")
    }

    // MARK: - Looking up what's showing (#172)

    @MainActor
    private func lookup(_ names: [String: String?], allowed: Bool = true, fails: Set<String> = [])
        -> (PlaceNameLookup, PlaceNameCache, Recorder) {
        let recorder = Recorder()
        let cache = PlaceNameCache(fileURL: cacheURL())
        let lookup = PlaceNameLookup(cache: cache, geocode: { latitude, longitude in
            let key = PlaceKey(latitude: latitude, longitude: longitude)!.text
            recorder.asked(key)
            if fails.contains(key) { throw URLError(.timedOut) }
            return names[key] ?? nil
        })
        lookup.sleep = { recorder.waited($0) }
        lookup.isAllowed = { allowed }
        return (lookup, cache, recorder)
    }

    private func key(_ latitude: Double, _ longitude: Double) -> PlaceKey { PlaceKey(latitude: latitude, longitude: longitude)! }

    @MainActor
    func testEachSpotIsAskedOnceOneAtATimeWithAGap() async {
        let (lookup, cache, recorder) = lookup(["1.00000,1.00000": "One", "2.00000,2.00000": nil])
        lookup.want(key(1, 1))
        lookup.want(key(1, 1))
        lookup.want(key(1.000001, 1))
        lookup.want(key(2, 2))
        lookup.want(nil)
        await lookup.whenIdle()
        XCTAssertEqual(recorder.keys, ["1.00000,1.00000", "2.00000,2.00000"])
        XCTAssertEqual(recorder.waits, [PlaceNameLookup.defaultGap, PlaceNameLookup.defaultGap])
        XCTAssertGreaterThanOrEqual(PlaceNameLookup.defaultGap, .seconds(1.2), "well under Apple's 50 a minute")
        XCTAssertEqual(cache.name(for: key(1, 1)), "One")
        XCTAssertEqual(cache.name(for: key(2, 2)), "")

        // Known now, with a name or without: not asked again.
        lookup.want(key(1, 1))
        lookup.want(key(2, 2))
        await lookup.whenIdle()
        XCTAssertEqual(recorder.keys.count, 2)
    }

    @MainActor
    func testNothingIsAskedWhenNotAllowed() async {
        let (lookup, _, recorder) = lookup(["1.00000,1.00000": "One"], allowed: false)
        lookup.want(key(1, 1))
        lookup.want(key(1, 1), first: true)
        await lookup.whenIdle()
        XCTAssertEqual(recorder.keys, [])
        XCTAssertEqual(lookup.pending, [])
    }

    @MainActor
    func testTurningOffStopsWhatsWaiting() async {
        let (lookup, _, recorder) = lookup([:])
        let allowed = Flag(true)
        lookup.isAllowed = { allowed.value }
        lookup.sleep = { _ in allowed.value = false }
        lookup.want(key(1, 1))
        lookup.want(key(2, 2))
        await lookup.whenIdle()
        XCTAssertEqual(recorder.keys, ["1.00000,1.00000"])
        XCTAssertEqual(lookup.pending, [])
    }

    @MainActor
    func testThePhotoYoureOnGoesFirstAndRowsScrolledAwayAreDropped() async {
        let (lookup, _, recorder) = lookup([:])
        // Wanted as rows come into view, in turn; nothing is sent until the main actor is free.
        lookup.want(key(1, 1))
        lookup.want(key(2, 2))
        lookup.want(key(3, 3))
        lookup.want(key(4, 4))
        lookup.unwant(key(3, 3))
        lookup.want(key(4, 4), first: true)
        await lookup.whenIdle()
        XCTAssertEqual(recorder.keys, ["4.00000,4.00000", "1.00000,1.00000", "2.00000,2.00000"])
    }

    @MainActor
    func testChangingFolderDropsWhatsWaiting() async {
        let (lookup, _, recorder) = lookup([:])
        lookup.want(key(1, 1))
        lookup.want(key(2, 2))
        lookup.clear()
        lookup.want(key(5, 5))
        await lookup.whenIdle()
        XCTAssertEqual(recorder.keys, ["5.00000,5.00000"])
    }

    @MainActor
    func testAFailureWaitsLongerAndIsntAskedAgainThisSession() async {
        let (lookup, cache, recorder) = lookup(["2.00000,2.00000": "Two"], fails: ["1.00000,1.00000"])
        lookup.want(key(1, 1))
        lookup.want(key(2, 2))
        await lookup.whenIdle()
        XCTAssertEqual(recorder.waits, [PlaceNameLookup.defaultFailurePause, PlaceNameLookup.defaultGap])
        XCTAssertNil(cache.name(for: key(1, 1)), "not remembered, so it's tried another time")
        lookup.want(key(1, 1))
        await lookup.whenIdle()
        XCTAssertEqual(recorder.keys, ["1.00000,1.00000", "2.00000,2.00000"])
    }

    @MainActor
    func testOnlyAScreenfulWaits() {
        let (lookup, _, _) = lookup([:])
        lookup.isAllowed = { true }
        for index in 0..<(PlaceNameLookup.maxPending + 20) { lookup.want(key(Double(index) / 10, 0)) }
        XCTAssertEqual(lookup.pending.count, PlaceNameLookup.maxPending)
        XCTAssertEqual(lookup.pending.last, key(Double(PlaceNameLookup.maxPending + 19) / 10, 0), "the latest are kept")
    }
}

/// Counts calls from any thread.
final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    var count: Int { lock.withLock { value } }
    func add() { lock.withLock { value += 1 } }
}

/// A switch a stand-in can turn off from any thread.
final class Flag: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Bool
    init(_ value: Bool) { stored = value }
    var value: Bool {
        get { lock.withLock { stored } }
        set { lock.withLock { stored = newValue } }
    }
}

/// What a stand-in geocoder was asked, and the waits between.
final class Recorder: @unchecked Sendable {
    private let lock = NSLock()
    private var askedKeys: [String] = []
    private var waitsSeen: [Duration] = []
    var keys: [String] { lock.withLock { askedKeys } }
    var waits: [Duration] { lock.withLock { waitsSeen } }
    func asked(_ key: String) { lock.withLock { askedKeys.append(key) } }
    func waited(_ duration: Duration) { lock.withLock { waitsSeen.append(duration) } }
}
