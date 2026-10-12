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

    func testSavingInPhotosOnlyFillsEmptyAccessibilityFields() {
        XCTAssertTrue(FileFields.mayWriteAccessibility("A dog.", altText: "", extendedDescription: "", policy: .whenEmpty))
        XCTAssertFalse(FileFields.mayWriteAccessibility("A dog.", altText: "A dog.", extendedDescription: "", policy: .whenEmpty))
        XCTAssertFalse(FileFields.mayWriteAccessibility("A dog.", altText: "Rex (Lightroom)", extendedDescription: "", policy: .whenEmpty))
        // IDT wrote an extended description but no alt text: left alone (#79).
        XCTAssertFalse(FileFields.mayWriteAccessibility("A dog.", altText: "", extendedDescription: "A long one from IDT", policy: .whenEmpty))
        XCTAssertFalse(FileFields.mayWriteAccessibility("A dog.", altText: "", extendedDescription: "", policy: .never))
    }

    func testAnExtendedDescriptionAnotherAppWroteIsAskedAbout() {
        let fields = FileFields.replaced(caption: "", altText: "", extendedDescription: "From IDT", with: "A dog.")
        XCTAssertEqual(fields.map(\.field), ["extended description"], "a short description would clear it")
    }

    func testTheTitleFollowsTheCaptionOnlyWhenItWasTheCaption() {
        XCTAssertTrue(CaptionTitle.keepsInStep(title: "", oldCaption: "Old"))
        XCTAssertTrue(CaptionTitle.keepsInStep(title: "Old", oldCaption: "Old"))
        XCTAssertFalse(CaptionTitle.keepsInStep(title: "Grandma's 80th", oldCaption: "Old"))
    }
}
