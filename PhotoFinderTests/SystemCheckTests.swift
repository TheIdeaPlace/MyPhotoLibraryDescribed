import Photos
import XCTest
@testable import Photo_Finder

final class SystemCheckTests: XCTestCase {
    private func facts(os: Int = 27, silicon: Bool = true, ai: String? = nil, photos: PHAuthorizationStatus = .authorized,
                       saved: Int = 0) -> SystemCheck.Facts {
        .init(appName: "Photo Finder", appVersion: "0.1.0 (1)",
              osVersion: OperatingSystemVersion(majorVersion: os, minorVersion: 1, patchVersion: 0),
              appleSilicon: silicon, aiUnavailableReason: ai, photosAccess: photos, savedDescriptions: saved,
              libraryCoverage: (described: saved, total: 2000),
              describeAutomatically: true, describeInBackground: false)
    }

    func testAReadyMacSaysEverythingIsReady() {
        let check = SystemCheck(facts(saved: 1234))
        XCTAssertEqual(check.summary, "Everything needed to describe photos is ready.")
        XCTAssertTrue(check.text.contains("Apple Intelligence: OK. Ready to describe photos, on this Mac."))
        XCTAssertTrue(check.text.contains("Saved descriptions: 61% described, 1,234 of 2,000 photos."))
        XCTAssertTrue(check.text.contains("Photo Finder: Version 0.1.0 (1)."))
    }

    func testEachProblemSaysWhatToDo() {
        let check = SystemCheck(facts(os: 26, silicon: false, ai: "Turn on Apple Intelligence in System Settings to describe images.",
                                      photos: .denied))
        XCTAssertEqual(check.summary, "3 things need attention.", "macOS 26 is one problem, not two")
        XCTAssertTrue(check.text.contains("Apple Intelligence: Not checked: describing needs macOS 27."))
        XCTAssertTrue(check.text.contains("macOS: Needs attention. macOS 26.1. Describing photos needs macOS 27 or later."))
        XCTAssertTrue(check.text.contains("Processor: Needs attention. Intel."))
        XCTAssertTrue(check.text.contains("Photos library: Needs attention. No access."))
    }

    func testAppleIntelligenceProblemsShowOnMacOS27() {
        let check = SystemCheck(facts(ai: "Turn on Apple Intelligence in System Settings to describe images."))
        XCTAssertTrue(check.text.contains("Apple Intelligence: Needs attention. Turn on Apple Intelligence"))
    }

    func testRestrictedAccessSaysWhoCanChangeIt() {
        XCTAssertTrue(SystemCheck(facts(photos: .restricted)).text.contains("Ask whoever manages this Mac."))
    }

    func testOneProblemIsSingular() {
        XCTAssertEqual(SystemCheck(facts(photos: .limited)).summary, "1 thing needs attention.")
    }

    func testTheBugReportIncludesTheSystemCheck() {
        let body = SystemCheck.bugReportTemplate(systemText: "macOS: OK. macOS 27.1.")
        XCTAssertTrue(body.contains("**What happened**"))
        XCTAssertTrue(body.contains("macOS: OK. macOS 27.1."))
    }

    func testCoverageSaysHowMuchOfTheLibraryIsDescribed() {
        XCTAssertEqual(SystemCheck.coverageText(described: 159, total: 400), "39% described, 159 of 400 photos.")
        XCTAssertEqual(SystemCheck.coverageText(described: 400, total: 400), "100% described, 400 of 400 photos.")
        // Rounded down: 100% only when every photo is described.
        XCTAssertEqual(SystemCheck.coverageText(described: 999, total: 1000), "99% described, 999 of 1,000 photos.")
        XCTAssertEqual(SystemCheck.coverageText(described: 0, total: 1), "0% described, 0 of 1 photo.")
        XCTAssertEqual(SystemCheck.coverageText(described: 0, total: 0), "No photos in the library yet.")
    }
}
