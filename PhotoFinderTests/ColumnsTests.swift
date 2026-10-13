import SwiftUI
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

final class OrderEditorTests: XCTestCase {
    func testMovingSaysWhereTheColumnWent() {
        var editor = OrderEditor(.columns, shown: ["name", "date", "caption"])
        editor.shownSelection = "caption"
        XCTAssertEqual(editor.move(by: -1), "Caption moved up, 2 of 3.")
        XCTAssertEqual(editor.shown, ["name", "caption", "date"])
        XCTAssertEqual(editor.moveToEnd(top: true), "Caption moved to top, 1 of 3.")
        XCTAssertEqual(editor.move(by: -1), "Caption is already at the top.")
        XCTAssertEqual(editor.moveToEnd(top: false), "Caption moved to bottom, 3 of 3.")
        XCTAssertEqual(editor.shownSelection, "caption")
    }

    func testAddingAndRemoving() {
        var editor = OrderEditor(.columns, shown: ["name", "date"])
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
        var editor = OrderEditor(.columns, shown: ColumnOrder.all.map(\.id))
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

final class ColumnMigrationTests: XCTestCase {
    func testOldTableCustomizationCarriesOver() throws {
        var old = TableColumnCustomization<FileRow>()
        old[visibility: "caption"] = .hidden
        old[visibility: "keywords"] = .visible
        let data = try JSONEncoder().encode(old)
        XCTAssertEqual(ColumnOrder.migrate(customization: data),
                       ["name", "date", "kind", "dimensions", "duration", "favorite", "keywords"])
        XCTAssertNil(ColumnOrder.migrate(customization: Data("not json".utf8)))
    }
}

final class BackgroundQueueTests: XCTestCase {
    func testProgressAndSummary() {
        var queue = BackgroundQueue()
        XCTAssertNil(queue.progress)
        XCTAssertEqual(queue.add(["a", "b", "c"]), 3)
        XCTAssertEqual(queue.add(["b", "d"]), 1)
        XCTAssertEqual(queue.progress, "Describing photos in the background, 1 of 4")
        XCTAssertEqual(queue.next(), "a")
        queue.finished(described: true)
        XCTAssertEqual(queue.progress, "Describing photos in the background, 2 of 4")
        XCTAssertEqual(queue.next(), "b")
        queue.skippedNotOnThisMac()
        XCTAssertEqual(queue.summary, "Finished describing 1 photo in the background. 1 photo wasn't described because it's only in iCloud.")
    }

    func testFiveFailuresInARowStopTheRunButOneSuccessResetsTheCount() {
        var queue = BackgroundQueue()
        queue.add(["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"])
        for _ in 0..<4 {
            _ = queue.next()
            XCTAssertFalse(queue.failed())
        }
        _ = queue.next()
        queue.finished(described: true)
        XCTAssertEqual(queue.failedInARow, 0)
        for _ in 0..<4 {
            _ = queue.next()
            XCTAssertFalse(queue.failed())
        }
        _ = queue.next()
        XCTAssertTrue(queue.failed())
        XCTAssertEqual(queue.progress, "Describing photos in the background, 10 of 10")
        XCTAssertEqual(BackgroundQueue.stoppedMessage(left: 3, reason: "The model isn't ready."),
                       "Background describing stopped: 5 photos in a row couldn't be described, so 3 photos weren't tried. The model isn't ready.")
    }

    func testAFolderJustAddedGoesAheadOfTheBacklog() {
        var queue = BackgroundQueue()
        queue.add(["old1", "old2"])
        XCTAssertEqual(queue.add(["new1", "old1", "new2"], first: true), 2)
        XCTAssertEqual(queue.next(), "new1")
        XCTAssertEqual(queue.next(), "new2")
        XCTAssertEqual(queue.next(), "old1")
    }

    func testABusyPhotoIsPutBackAtTheFrontWithoutCountingAgain() {
        var queue = BackgroundQueue()
        queue.add(["a", "b"])
        XCTAssertEqual(queue.next(), "a")
        queue.putBack("a")
        XCTAssertEqual(queue.total, 2)
        XCTAssertEqual(queue.next(), "a")
        XCTAssertEqual(queue.next(), "b")
    }

    func testStopEmptiesTheQueueAndSaysHowManyWereLeft() {
        var queue = BackgroundQueue()
        queue.add(["a", "b", "c"])
        _ = queue.next()
        XCTAssertEqual(queue.stop(), 2)
        XCTAssertTrue(queue.isEmpty)
        XCTAssertNil(queue.progress)
    }
}
