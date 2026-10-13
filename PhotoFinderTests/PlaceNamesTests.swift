import XCTest
@testable import Photo_Finder

final class PlaceNamesTests: XCTestCase {
    func testOffUntilTurnedOn() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "PlaceNamesTests"))
        defaults.removePersistentDomain(forName: "PlaceNamesTests")
        defer { defaults.removePersistentDomain(forName: "PlaceNamesTests") }
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

    func testAPlaceIsNamedWithoutRepeatsOrGaps() {
        XCTAssertEqual(PlaceNames.joined(["Golden Gate Park", "San Francisco", "CA", "United States"]),
                       "Golden Gate Park, San Francisco, CA, United States")
        XCTAssertEqual(PlaceNames.joined(["Cupertino", "Cupertino", "CA", "United States"]), "Cupertino, CA, United States")
        XCTAssertEqual(PlaceNames.joined([nil, "Paris", " ", "France"]), "Paris, France")
        XCTAssertNil(PlaceNames.joined([nil, ""]))
    }
}
