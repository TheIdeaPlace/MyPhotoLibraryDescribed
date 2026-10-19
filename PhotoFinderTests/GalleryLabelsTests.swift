import XCTest
@testable import Photo_Finder

final class GalleryLabelsTests: XCTestCase {
    private func row(caption: String = "", keywords: [String] = [], rating: Int = 0, place: Bool = false) -> FileRow {
        var metadata = FileMetadata()
        metadata.width = 4032
        metadata.height = 3024
        metadata.dateTaken = Date(timeIntervalSince1970: 1_600_000_000)
        metadata.caption = caption
        metadata.keywords = keywords
        metadata.rating = rating
        if place {
            metadata.latitude = 47.6062
            metadata.longitude = -122.3321
        }
        let file = MediaFile(path: "/p/IMG_1234.HEIC", size: 1, created: nil, modified: Date(timeIntervalSince1970: 0))
        return FileRow(file: file, metadata: metadata)
    }

    func testTheDefaultIsPositionOnly() {
        XCTAssertEqual(GalleryLabels.defaultOrder, [.position])
        XCTAssertEqual(GalleryLabels.text(for: row(), position: (12, 400), items: GalleryLabels.defaultOrder), "12 of 400")
    }

    func testItemsAreReadInTheChosenOrder() {
        let photo = row(caption: "At the beach", keywords: ["sea", "sand"], rating: 3, place: true)
        XCTAssertEqual(GalleryLabels.text(for: photo, position: (1, 2), items: [.name, .position]), "IMG_1234.HEIC, 1 of 2")
        XCTAssertEqual(
            GalleryLabels.text(for: photo, position: (1, 2),
                               items: [.caption, .kind, .dimensions, .keywords, .rating, .location, .date]),
            "Caption: At the beach, Photo, 4032 × 3024, Keywords: sea, sand, 3 stars, Place taken 47.6062, -122.3321, \(photo.dateText)")
    }

    func testEmptyItemsAreLeftOutAndTheNameStandsInForNothing() {
        let photo = row()
        XCTAssertEqual(GalleryLabels.text(for: photo, position: (3, 9), items: [.caption, .position, .rating]), "3 of 9")
        XCTAssertEqual(GalleryLabels.text(for: photo, position: (3, 9), items: [.caption, .keywords]), "IMG_1234.HEIC")
    }

    func testCleanDropsUnknownAndRepeatedItems() {
        XCTAssertEqual(GalleryLabels.clean(["name", "nonsense", "position", "name"]), [.name, .position])
        XCTAssertEqual(GalleryLabels.clean([]), [.position])
        XCTAssertEqual(GalleryLabels.clean(nil), [.position])
    }

    func testTheGalleryTabUsesTheSameEditor() {
        var editor = OrderEditor(.gallery, shown: ["position"])
        XCTAssertEqual(editor.remove(), "Position is the only label shown. Add another before removing it.")
        editor.availableSelection = "name"
        XCTAssertEqual(editor.add(), "Name added, 2 of 2.")
        XCTAssertEqual(editor.moveToEnd(top: true), "Name moved to top, 1 of 2.")
        XCTAssertEqual(editor.shown, ["name", "position"])
        editor.shownSelection = "position"
        XCTAssertEqual(editor.remove(), "Position removed. 1 label shown.")
        XCTAssertEqual(editor.reset(), "Labels reset to the default: Position.")
        XCTAssertEqual(editor.shown, ["position"])
        XCTAssertEqual(editor.notShown.count, GalleryItem.allCases.count - 1)
    }
}
