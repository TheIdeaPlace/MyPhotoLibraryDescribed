import XCTest
@testable import Photo_Finder

final class RatingTests: XCTestCase {
    func testRatingsAreSaidInWindowsWords() {
        XCTAssertEqual(RatingText.done(0), "Rating removed")
        XCTAssertEqual(RatingText.done(1), "Rated 1 star")
        XCTAssertEqual(RatingText.done(4), "Rated 4 stars")
        XCTAssertEqual((0...5).map(RatingText.menuTitle), ["No Rating", "1 Star", "2 Stars", "3 Stars", "4 Stars", "5 Stars"])
    }
}
