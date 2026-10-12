import XCTest
@testable import Photo_Finder

final class CaptionPolicyTests: XCTestCase {
    func testNeverWritesACaption() {
        XCTAssertFalse(CaptionPolicy.never.shouldWrite("A dog on a beach", over: ""))
    }

    func testOnlyWhenEmptyLeavesPeoplesCaptionsAlone() {
        XCTAssertTrue(CaptionPolicy.whenEmpty.shouldWrite("A dog on a beach", over: ""))
        XCTAssertTrue(CaptionPolicy.whenEmpty.shouldWrite("A dog on a beach", over: "  \n"))
        XCTAssertFalse(CaptionPolicy.whenEmpty.shouldWrite("A dog on a beach", over: "Rex at Brighton"))
    }

    func testAlwaysReplacesButNotWithTheSameOrNothing() {
        XCTAssertTrue(CaptionPolicy.always.shouldWrite("A dog on a beach", over: "Rex at Brighton"))
        XCTAssertFalse(CaptionPolicy.always.shouldWrite("A dog on a beach", over: "A dog on a beach "))
        XCTAssertFalse(CaptionPolicy.always.shouldWrite("   ", over: "Rex"))
    }

    func testTheSettingIsOffUnlessChosen() throws {
        // A suite of its own: tests run inside the app, and the developer's own setting must survive them.
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "CaptionPolicyTests"))
        defaults.removePersistentDomain(forName: "CaptionPolicyTests")
        XCTAssertEqual(CaptionPolicy.current(in: defaults), .never)
        defaults.set("whenEmpty", forKey: CaptionPolicy.key)
        XCTAssertEqual(CaptionPolicy.current(in: defaults), .whenEmpty)
        defaults.removePersistentDomain(forName: "CaptionPolicyTests")
    }
}
