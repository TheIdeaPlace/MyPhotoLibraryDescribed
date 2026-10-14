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

    func testTheSavedFileHoldsTheCorrectionInItsRecord() throws {
        let url = folder().appendingPathComponent("Descriptions.json")
        let cache = DescriptionCache(fileURL: url)
        cache.remember("Mine", forKey: "photo", edited: true)
        cache.save()
        let saved = try ProvenanceFile.records(at: url)
        XCTAssertEqual(saved.keys.sorted(), ["photo"])
        XCTAssertEqual(saved["photo"]?.text, "Mine")
        XCTAssertEqual(saved["photo"]?.source, "person")
        XCTAssertNotNil(saved["photo"]?.edited)
        XCTAssertNil(saved["photo"]?.original, "nothing was there to correct")
    }

    func testDescribeAgainReplacesOnlyTheCorrectionAgreedTo() {
        XCTAssertTrue(DescriptionCache.mayReplace(edited: false, saved: "A dog", agreed: nil))
        XCTAssertFalse(DescriptionCache.mayReplace(edited: true, saved: "Rex", agreed: nil), "a correction is kept")
        XCTAssertTrue(DescriptionCache.mayReplace(edited: true, saved: "Rex", agreed: "Rex"))
        XCTAssertFalse(DescriptionCache.mayReplace(edited: true, saved: "Rex on the beach", agreed: "Rex"),
                       "corrected again after agreeing: the newer correction is kept")
    }

    func testOnlyWhatThePersonTypedCountsAsAChange() {
        XCTAssertFalse(DescriptionDraft.isChanged("A dog", from: "A dog"))
        XCTAssertFalse(DescriptionDraft.isChanged("A dog \n", from: "A dog"), "whitespace isn't a change")
        XCTAssertFalse(DescriptionDraft.isChanged("", from: ""), "tabbing through an empty box isn't a change")
        XCTAssertTrue(DescriptionDraft.isChanged("A brown dog", from: "A dog"))
        XCTAssertTrue(DescriptionDraft.isChanged("", from: "A dog"))
    }

    func testTheDescriptionColumnShowsAndIsReadWithItsLabel() {
        XCTAssertEqual(ColumnOrder.title("description"), "Description")
        XCTAssertFalse(ColumnOrder.defaultOrder.contains("description"), "off until chosen in View > Columns")
    }
}
