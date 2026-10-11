import XCTest
@testable import Photo_Finder

final class PaneTests: XCTestCase {
    func testF6GoesSidebarContentSearchStatusBarAndRound() {
        XCTAssertEqual(Pane.next(from: .sidebar, by: 1), .content)
        XCTAssertEqual(Pane.next(from: .content, by: 1), .search)
        XCTAssertEqual(Pane.next(from: .search, by: 1), .status(.status))
        XCTAssertEqual(Pane.next(from: .status(.background), by: 1), .sidebar)
        XCTAssertEqual(Pane.next(from: .sidebar, by: -1), .status(.status))
        XCTAssertEqual(Pane.next(from: .status(.activity), by: -1), .search)
        XCTAssertEqual(Pane.next(from: nil, by: 1), .search)
    }

    func testLeftAndRightWrapThroughTheSectionsShown() {
        let all = StatusSection.allCases
        XCTAssertEqual(StatusSection.next(from: .status, by: 1, shown: all), .activity)
        XCTAssertEqual(StatusSection.next(from: .background, by: 1, shown: all), .status)
        XCTAssertEqual(StatusSection.next(from: .status, by: -1, shown: all), .background)
        XCTAssertEqual(StatusSection.next(from: .status, by: 1, shown: [.status, .background]), .background)
        XCTAssertEqual(StatusSection.next(from: .status, by: 1, shown: [.status]), .status)
        // The section you were on went away: back to the first one shown.
        XCTAssertEqual(StatusSection.next(from: .activity, by: 1, shown: [.status, .background]), .status)
    }
}
