import XCTest
@testable import Photo_Finder

final class CaptionPolicyTests: XCTestCase {
    func testOffWritesNothing() {
        XCTAssertFalse(CaptionPolicy.never.shouldWrite("A dog on a beach", over: ""))
    }

    func testOnOnlyFillsEmptyCaptionsAndNeverReplacesAPersons() {
        XCTAssertTrue(CaptionPolicy.whenEmpty.shouldWrite("A dog on a beach", over: ""))
        XCTAssertTrue(CaptionPolicy.whenEmpty.shouldWrite("A dog on a beach", over: "  \n"))
        XCTAssertFalse(CaptionPolicy.whenEmpty.shouldWrite("A dog on a beach", over: "Rex at Brighton"))
        XCTAssertFalse(CaptionPolicy.whenEmpty.shouldWrite("   ", over: ""))
    }

    private func defaults() throws -> UserDefaults {
        // A suite of its own: tests run inside the app, and the developer's own setting must survive them.
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "CaptionPolicyTests"))
        defaults.removePersistentDomain(forName: "CaptionPolicyTests")
        return defaults
    }

    func testItIsOffUntilTurnedOn() throws {
        let defaults = try defaults()
        XCTAssertFalse(CaptionPolicy.isOn(in: defaults))
        XCTAssertEqual(CaptionPolicy.current(in: defaults), .never)
        defaults.set(true, forKey: CaptionPolicy.key)
        XCTAssertEqual(CaptionPolicy.current(in: defaults), .whenEmpty)
    }

    func testTheOldThreeWaySettingCarriesOver() throws {
        let defaults = try defaults()
        defaults.set("never", forKey: CaptionPolicy.oldKey)
        XCTAssertFalse(CaptionPolicy.isOn(in: defaults))
        defaults.set("whenEmpty", forKey: CaptionPolicy.oldKey)
        XCTAssertTrue(CaptionPolicy.isOn(in: defaults))
        defaults.set("always", forKey: CaptionPolicy.oldKey)
        XCTAssertTrue(CaptionPolicy.isOn(in: defaults), "Always becomes the safer on")
        defaults.set(false, forKey: CaptionPolicy.key)
        XCTAssertFalse(CaptionPolicy.isOn(in: defaults), "the new switch wins once set")
    }
}
