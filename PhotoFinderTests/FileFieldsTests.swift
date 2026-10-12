import XCTest
@testable import Photo_Finder

final class FileFieldsTests: XCTestCase {
    func testAltTextAnotherAppWroteIsAskedAbout() {
        let fields = FileFields.replaced(caption: "", altText: "Rex on the beach (Lightroom)", with: "A brown dog on sand.")
        XCTAssertEqual(fields.map(\.field), ["alt text"])
    }

    func testEmptyOrMatchingFieldsAreNotAskedAbout() {
        XCTAssertTrue(FileFields.replaced(caption: "", altText: "", with: "A dog.").isEmpty)
        XCTAssertTrue(FileFields.replaced(caption: "A dog.", altText: "A dog.", with: "A dog.").isEmpty)
        XCTAssertTrue(FileFields.replaced(caption: nil, altText: "", with: "A dog.").isEmpty)
    }

    func testACaptionAndAltTextBothDifferentAreBothListed() {
        let fields = FileFields.replaced(caption: "Holiday", altText: "Old alt text", with: "A dog.")
        XCTAssertEqual(fields.map(\.field), ["caption", "alt text"])
    }

    func testAutomaticCaptionsLeaveOtherAppsAltTextAloneUnlessAlways() {
        XCTAssertTrue(FileFields.mayWriteAltText("A dog.", over: "", policy: .whenEmpty))
        XCTAssertFalse(FileFields.mayWriteAltText("A dog.", over: "A dog.", policy: .whenEmpty), "already there")
        XCTAssertFalse(FileFields.mayWriteAltText("A dog.", over: "A dog.", policy: .always), "already there")
        XCTAssertFalse(FileFields.mayWriteAltText("A dog.", over: "Rex (Lightroom)", policy: .whenEmpty))
        XCTAssertTrue(FileFields.mayWriteAltText("A dog.", over: "Rex (Lightroom)", policy: .always))
        XCTAssertFalse(FileFields.mayWriteAltText("A dog.", over: "", policy: .never))
    }
}
