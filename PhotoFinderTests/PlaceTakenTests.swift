import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import Photo_Finder

/// The list's Place Taken column and the gallery's Place taken label naming places (#172), through an added folder of
/// temporary photos with coordinates. A stand-in answers for Apple; nothing is sent, and the real data folder and
/// settings aren't touched.
@MainActor
final class PlaceTakenTests: XCTestCase {
    /// Seattle and the open sea.
    private let seattle = (latitude: 47.6097, longitude: -122.3422)
    private let sea = (latitude: 10.0, longitude: -140.0)

    private func makeLibrary(lookUp: Bool, columns: [String] = ["name", "location"], gallery: [String]? = nil,
                             viewMode: String = "list", seattleName: String = "Pike Place Market, Seattle, WA, United States",
                             sortColumn: String = "name") async throws -> (TestLibrary, Recorder) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("PlaceTakenTests-\(UUID().uuidString)")
        let photos = base.appendingPathComponent("Photos", isDirectory: true)
        try FileManager.default.createDirectory(at: photos, withIntermediateDirectories: true)
        try writeJPEG(to: photos.appendingPathComponent("seattle.jpg"), at: seattle)
        try writeJPEG(to: photos.appendingPathComponent("sea.jpg"), at: sea)
        try writeJPEG(to: photos.appendingPathComponent("nowhere.jpg"), at: nil)

        let suite = "PlaceTakenTests"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defaults.removePersistentDomain(forName: suite)
        defaults.set(sortColumn, forKey: ColumnOrder.sortColumnKey)
        defaults.set(false, forKey: ColumnOrder.sortDescendingKey)
        defaults.set(lookUp, forKey: PlaceNames.key)
        defaults.set(columns, forKey: "columnOrder")
        defaults.set(viewMode, forKey: "viewMode")
        if let gallery { defaults.set(gallery, forKey: GalleryLabels.defaultsKey) }
        addTeardownBlock {
            UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite)
            try? FileManager.default.removeItem(at: base)
        }

        let recorder = Recorder()
        let root = FolderRoots.normalize(photos.path)
        let library = TestLibrary(root: root, defaults: defaults,
                                  cacheURL: base.appendingPathComponent("Store").appendingPathComponent("Descriptions.json"),
                                  geocodePlace: { latitude, longitude in
            let key = PlaceKey(latitude: latitude, longitude: longitude)!.text
            recorder.asked(key)
            return key == "47.60970,-122.34220" ? seattleName : nil
        })
        let model = library.model
        // Written now, before the folder is removed (teardown runs last first), not into it again two seconds later.
        let placeNames = model.placeNames
        addTeardownBlock { placeNames.flush() }
        model.placeLookup.sleep = { recorder.waited($0) }
        model.addFolders([root])
        try await waitUntil("the folder in the sidebar") { model.itemsByID[FolderID.folder(root)] != nil }
        model.selectedSourceID = FolderID.folder(root)
        try await waitUntil("the photos' coordinates") {
            !model.isLoading && model.rows.count == 3 && model.rows.filter { $0.placeKey != nil }.count == 2
        }
        library.said.removeAll()
        return (library, recorder)
    }

    private func row(_ library: TestLibrary, _ name: String) -> FileRow {
        library.model.rows.first { $0.name == name }!
    }

    func testTheColumnNamesThePlacesShowingWithoutSayingAnything() async throws {
        let (library, recorder) = try await makeLibrary(lookUp: true)
        let model = library.model
        XCTAssertEqual(row(library, "seattle.jpg").text(for: "location"), "47.6097, -122.3422")

        for name in ["seattle.jpg", "sea.jpg", "nowhere.jpg", "seattle.jpg"] { model.placeCellAppeared(row(library, name)) }
        try await waitUntil("the place's name in the column") {
            library.model.rows.first { $0.name == "seattle.jpg" }?.text(for: "location")
                == "Pike Place Market, Seattle, WA, United States"
        }
        await model.placeLookup.whenIdle()
        XCTAssertEqual(recorder.keys.sorted(), ["10.00000,-140.00000", "47.60970,-122.34220"], "each spot once, none without coordinates")
        XCTAssertEqual(row(library, "sea.jpg").text(for: "location"), "10.0000, -140.0000", "no name: the coordinates")
        XCTAssertEqual(row(library, "nowhere.jpg").text(for: "location"), "")
        XCTAssertTrue(library.said.isEmpty, "\(library.said)")

        // Sorted by Place Taken: by what it shows.
        model.sortBy("location")
        XCTAssertEqual(model.rows.map(\.name), ["nowhere.jpg", "sea.jpg", "seattle.jpg"])

        // Turned off: the coordinates again, and nothing more is asked.
        model.setLookUpPlaceNames(false)
        XCTAssertEqual(row(library, "seattle.jpg").text(for: "location"), "47.6097, -122.3422")
        model.placeCellAppeared(row(library, "seattle.jpg"))
        await model.placeLookup.whenIdle()
        XCTAssertEqual(recorder.keys.count, 2)
    }

    func testNothingIsAskedWhenLookUpPlaceNamesIsOff() async throws {
        let (library, recorder) = try await makeLibrary(lookUp: false)
        library.model.placeCellAppeared(row(library, "seattle.jpg"))
        library.model.selection = [row(library, "sea.jpg").id]
        await library.model.placeLookup.whenIdle()
        XCTAssertEqual(recorder.keys, [])
    }

    func testNothingIsAskedWhenPlaceTakenIsntShowing() async throws {
        let (library, recorder) = try await makeLibrary(lookUp: true, columns: ["name", "date"], gallery: ["position", "location"])
        let model = library.model
        model.placeCellAppeared(row(library, "seattle.jpg"))
        model.selection = [row(library, "sea.jpg").id]
        await model.placeLookup.whenIdle()
        XCTAssertEqual(recorder.keys, [], "the list doesn't show Place Taken")

        // The gallery does: the photo you're on is looked up.
        model.viewMode = .gallery
        model.selection = [row(library, "seattle.jpg").id]
        try await waitUntil("the gallery's label to name the place") {
            model.focusedRow.map { model.galleryText(for: $0, at: 0, of: 3) }
                == "1 of 3, Place taken Pike Place Market, Seattle, WA, United States"
        }
        XCTAssertFalse(library.said.contains { $0.text.contains("Pike Place") }, "the name isn't spoken as it arrives")
    }

    func testTheGalleryLooksUpTheCurrentPhotoOnlyWithItsLabelShown() async throws {
        let (library, recorder) = try await makeLibrary(lookUp: true, columns: ["name", "location"], viewMode: "gallery")
        let model = library.model
        model.selection = [row(library, "seattle.jpg").id]
        await model.placeLookup.whenIdle()
        XCTAssertEqual(recorder.keys, [], "the gallery's labels don't include Place Taken")
        model.galleryLabels = [.position, .location]
        await model.placeLookup.whenIdle()
        XCTAssertEqual(recorder.keys, ["47.60970,-122.34220"])
    }

    func testRowsDontMoveWhenANameArrivesOnlyWhenSorted() async throws {
        // Sorted by Place Taken; the name found for Seattle sorts ahead of the sea's coordinates.
        let (library, _) = try await makeLibrary(lookUp: true, seattleName: "01 Pike Street, Seattle", sortColumn: "location")
        let model = library.model
        XCTAssertEqual(model.rows.map(\.name), ["nowhere.jpg", "sea.jpg", "seattle.jpg"])
        model.selection = [row(library, "seattle.jpg").id]
        try await waitUntil("the name in the column") {
            library.model.rows.first { $0.name == "seattle.jpg" }?.placeName == "01 Pike Street, Seattle"
        }
        XCTAssertEqual(model.rows.map(\.name), ["nowhere.jpg", "sea.jpg", "seattle.jpg"], "nothing moves under VoiceOver")

        // Sorting again (the column's header, or Sort By) sorts by the name.
        model.sortOrder = model.sortOrder
        XCTAssertEqual(model.rows.map(\.name), ["nowhere.jpg", "seattle.jpg", "sea.jpg"])
    }

    func testPhotosPassedInTheGalleryArentLookedUp() async throws {
        let (library, recorder) = try await makeLibrary(lookUp: true, gallery: ["position", "location"], viewMode: "gallery")
        let model = library.model
        model.selection = [row(library, "sea.jpg").id]
        model.selection = [row(library, "nowhere.jpg").id]
        model.selection = [row(library, "seattle.jpg").id]
        await model.placeLookup.whenIdle()
        XCTAssertEqual(recorder.keys, ["47.60970,-122.34220"])
    }

    private func writeJPEG(to url: URL, at place: (latitude: Double, longitude: Double)?) throws {
        let context = try XCTUnwrap(CGContext(data: nil, width: 8, height: 6, bitsPerComponent: 8, bytesPerRow: 0,
                                              space: CGColorSpaceCreateDeviceRGB(),
                                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        let destination = try XCTUnwrap(CGImageDestinationCreateWithURL(url as CFURL, UTType.jpeg.identifier as CFString, 1, nil))
        var properties: [CFString: Any] = [:]
        if let place {
            properties[kCGImagePropertyGPSDictionary] = [
                kCGImagePropertyGPSLatitude: abs(place.latitude),
                kCGImagePropertyGPSLatitudeRef: place.latitude < 0 ? "S" : "N",
                kCGImagePropertyGPSLongitude: abs(place.longitude),
                kCGImagePropertyGPSLongitudeRef: place.longitude < 0 ? "W" : "E",
            ] as [CFString: Any]
        }
        CGImageDestinationAddImage(destination, try XCTUnwrap(context.makeImage()), properties as CFDictionary)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
    }
}
