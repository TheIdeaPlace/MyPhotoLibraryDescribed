import XCTest
@testable import Photo_Finder

final class ColumnOrderTests: XCTestCase {
    func testCleanDropsUnknownAndRepeatedAndKeepsName() {
        XCTAssertEqual(ColumnOrder.clean(["caption", "nonsense", "name", "date", "caption"]), ["caption", "name", "date"])
        XCTAssertEqual(ColumnOrder.clean(["caption"]), ["name", "caption"])
        XCTAssertEqual(ColumnOrder.clean([]), ColumnOrder.defaultOrder)
        XCTAssertEqual(ColumnOrder.clean(nil), ColumnOrder.defaultOrder)
    }

    func testDefaultOrderIsNameThenTheDefaults() {
        XCTAssertEqual(ColumnOrder.defaultOrder, ["name", "date", "kind", "caption", "dimensions", "duration", "favorite"])
    }

    func testOldVisibilitySettingsCarryOver() {
        let order = ColumnOrder.fromVisibility { column in
            switch column.id {
            case "caption": false
            case "keywords": true
            default: nil
            }
        }
        XCTAssertEqual(order, ["name", "date", "kind", "dimensions", "duration", "favorite", "keywords"])
    }
}

final class ColumnsEditorTests: XCTestCase {
    func testMovingSaysWhereTheColumnWent() {
        var editor = ColumnsEditor(shown: ["name", "date", "caption"])
        editor.shownSelection = "caption"
        XCTAssertEqual(editor.move(by: -1), "Caption moved up, 2 of 3.")
        XCTAssertEqual(editor.shown, ["name", "caption", "date"])
        XCTAssertEqual(editor.moveToEnd(top: true), "Caption moved to top, 1 of 3.")
        XCTAssertEqual(editor.move(by: -1), "Caption is already at the top.")
        XCTAssertEqual(editor.moveToEnd(top: false), "Caption moved to bottom, 3 of 3.")
        XCTAssertEqual(editor.shownSelection, "caption")
    }

    func testAddingAndRemoving() {
        var editor = ColumnsEditor(shown: ["name", "date"])
        editor.availableSelection = "rating"
        XCTAssertEqual(editor.add(), "Rating added, 3 of 3.")
        XCTAssertEqual(editor.shown, ["name", "date", "rating"])
        XCTAssertFalse(editor.notShown.contains("rating"))
        editor.shownSelection = "date"
        XCTAssertEqual(editor.remove(), "Date Taken removed. 2 columns shown.")
        editor.shownSelection = "name"
        XCTAssertEqual(editor.remove(), "Name is always shown. It can be moved.")
        XCTAssertEqual(editor.shown, ["name", "rating"])
    }

    func testEveryColumnShownAndReset() {
        var editor = ColumnsEditor(shown: ColumnOrder.all.map(\.id))
        XCTAssertEqual(editor.add(), "Every column is shown already.")
        XCTAssertEqual(editor.reset(), "Columns reset to the default: \(ColumnOrder.defaultOrder.count) shown, Name first.")
        XCTAssertEqual(editor.shown, ColumnOrder.defaultOrder)
    }
}

final class DescriptionCacheTests: XCTestCase {
    func testDescriptionsSurviveARestart() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString).appendingPathComponent("d.json")
        let cache = DescriptionCache(fileURL: url)
        cache.remember("  A cat on a sofa.  ", forKey: "photo-1")
        cache.remember("   ", forKey: "photo-2")
        cache.save()
        let reloaded = DescriptionCache(fileURL: url)
        XCTAssertEqual(reloaded.description(forKey: "photo-1"), "A cat on a sofa.")
        XCTAssertNil(reloaded.description(forKey: "photo-2"))
    }
}
