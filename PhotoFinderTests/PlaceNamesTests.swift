import XCTest
@testable import Photo_Finder

final class PlaceNamesTests: XCTestCase {
    func testOffUntilTurnedOn() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "PlaceNamesTests-\(UUID().uuidString)"))
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
}
