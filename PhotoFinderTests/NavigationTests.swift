import XCTest
@testable import Photo_Finder

final class NavigationTests: XCTestCase {
    func testSearchResultsAreCountedAsOnWindows() {
        XCTAssertEqual(LibraryModel.resultCount(0), "No results")
        XCTAssertEqual(LibraryModel.resultCount(1), "1 result")
        XCTAssertEqual(LibraryModel.resultCount(12), "12 results")
    }

    func testDatesAndSizesSortNewestOrLargestFirstAndTextAToZ() {
        XCTAssertTrue(ColumnOrder.sortsDescendingFirst("date"))
        XCTAssertTrue(ColumnOrder.sortsDescendingFirst("megapixels"))
        XCTAssertFalse(ColumnOrder.sortsDescendingFirst("name"))
        XCTAssertFalse(ColumnOrder.sortsDescendingFirst("kind"))
    }

    func testTheSortColumnIsFoundWhicheverWayItRuns() {
        var byDate = ColumnOrder.comparator("date")
        byDate.order = .reverse
        XCTAssertEqual(ColumnOrder.sortColumn(of: [byDate]), "date")
        XCTAssertEqual(ColumnOrder.sortColumn(of: [ColumnOrder.comparator("name")]), "name")
        XCTAssertEqual(ColumnOrder.sortColumn(of: [ColumnOrder.comparator("location")]), "location")
        XCTAssertNil(ColumnOrder.sortColumn(of: []))
    }

    func testEveryColumnCanBeSortedBy() {
        for column in ColumnOrder.all {
            XCTAssertEqual(ColumnOrder.sortColumn(of: [ColumnOrder.comparator(column.id)]), column.id, column.title)
        }
    }

    func testTheSortIsRememberedBetweenLaunches() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "NavigationTests.sort"))
        defaults.removePersistentDomain(forName: "NavigationTests.sort")
        XCTAssertEqual(ColumnOrder.sortColumn(of: ColumnOrder.savedSort(defaults)), "date")
        XCTAssertEqual(ColumnOrder.savedSort(defaults).first?.order, .reverse, "newest first, as Windows starts")

        var byName = ColumnOrder.comparator("name")
        byName.order = .reverse
        ColumnOrder.saveSort([byName], to: defaults)
        let restored = ColumnOrder.savedSort(defaults)
        XCTAssertEqual(ColumnOrder.sortColumn(of: restored), "name")
        XCTAssertEqual(restored.first?.order, .reverse)

        defaults.set("noSuchColumn", forKey: ColumnOrder.sortColumnKey)
        XCTAssertEqual(ColumnOrder.sortColumn(of: ColumnOrder.savedSort(defaults)), "date")
        defaults.removePersistentDomain(forName: "NavigationTests.sort")
    }
}
