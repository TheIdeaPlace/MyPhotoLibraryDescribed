import XCTest
@testable import Photo_Finder

final class DescriptionPanelTests: XCTestCase {
    private func folder() -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    }

    func testACorrectionIsMarkedEditedAndKeptAfterSaving() {
        let url = folder().appendingPathComponent("Descriptions.json")
        let cache = DescriptionCache(fileURL: url)
        cache.remember("A dog", forKey: "photo")
        XCTAssertFalse(cache.isEdited(forKey: "photo"))
        cache.remember("Rex, our brown dog, on the beach", forKey: "photo", edited: true)
        XCTAssertTrue(cache.isEdited(forKey: "photo"))
        cache.save()
        let reopened = DescriptionCache(fileURL: url)
        XCTAssertEqual(reopened.description(forKey: "photo"), "Rex, our brown dog, on the beach")
        XCTAssertTrue(reopened.isEdited(forKey: "photo"))
    }

    func testDescribingAgainClearsTheEditedMark() {
        let cache = DescriptionCache(fileURL: folder().appendingPathComponent("Descriptions.json"))
        cache.remember("Mine", forKey: "photo", edited: true)
        cache.remember("A new description from Apple Intelligence", forKey: "photo")
        XCTAssertFalse(cache.isEdited(forKey: "photo"))
    }

    func testTheSavedFileKeepsItsFormatForWindows() throws {
        let url = folder().appendingPathComponent("Descriptions.json")
        let cache = DescriptionCache(fileURL: url)
        cache.remember("Mine", forKey: "photo", edited: true)
        cache.save()
        let saved = try JSONDecoder().decode([String: String].self, from: Data(contentsOf: url))
        XCTAssertEqual(saved, ["photo": "Mine"])
    }

    func testTheDescriptionColumnShowsAndIsReadWithItsLabel() {
        XCTAssertEqual(ColumnOrder.title("description"), "Description")
        XCTAssertFalse(ColumnOrder.defaultOrder.contains("description"), "off until chosen in View > Columns")
    }
}
