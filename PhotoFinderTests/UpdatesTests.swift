import XCTest
@testable import Photo_Finder

final class UpdatesTests: XCTestCase {
    func testVersionsCompareNumberByNumber() {
        XCTAssertTrue(Updates.isNewer("0.10.0", than: "0.9.1"))
        XCTAssertTrue(Updates.isNewer("1.0", than: "0.99.9"))
        XCTAssertTrue(Updates.isNewer("0.1.1", than: "0.1"))
        XCTAssertFalse(Updates.isNewer("0.1.0", than: "0.1"))
        XCTAssertFalse(Updates.isNewer("0.1.0", than: "0.2.0"))
    }

    private func release(_ tag: String, draft: Bool = false, prerelease: Bool = false) -> [String: Any] {
        ["tag_name": tag, "draft": draft, "prerelease": prerelease,
         "html_url": "https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/releases/tag/\(tag)"]
    }

    func testTheNewestReleaseIsFound_FromBothTheSharedTagsAndTheMacsEarlierOnes() {
        let list = [release("mac-v0.1.1"), release("v0.3.0", draft: true), release("v0.4.0", prerelease: true),
                    release("v0.2.0"), release("windows-v0.9.0"), release("mac-v0.1.0"), release("vNext")]
        XCTAssertEqual(Updates.newer(than: "0.1.0", in: list)?.version, "0.2.0")
        XCTAssertNil(Updates.newer(than: "0.2.0", in: list), "up to date")
        XCTAssertEqual(Updates.version(fromTag: "mac-v0.1.1"), "0.1.1")
        XCTAssertEqual(Updates.version(fromTag: "v0.2.0"), "0.2.0")
        XCTAssertNil(Updates.version(fromTag: "windows-v0.2.0"))
        XCTAssertNil(Updates.version(fromTag: "vNext"))
    }

    func testTheMenuAlwaysSaysWhatsRunning() {
        XCTAssertEqual(Updates.menuTitle(running: "0.1.1", available: nil), "Check for Updates (running 0.1.1)…")
        XCTAssertEqual(Updates.menuTitle(running: "0.1.1", available: "0.2.0"), "Update to 0.2.0 available…")
    }

    func testUpdatedIsSaidOnlyAfterAnUpdate() {
        XCTAssertFalse(Updates.justUpdated(last: nil, current: "0.1.1"), "first run")
        XCTAssertFalse(Updates.justUpdated(last: "0.1.1", current: "0.1.1"), "same version")
        XCTAssertFalse(Updates.justUpdated(last: "0.2.0", current: "0.1.1"), "went back")
        XCTAssertTrue(Updates.justUpdated(last: "0.1.0", current: "0.1.1"))
    }

    func testTheRepositoryIsNamedOnce() {
        XCTAssertTrue(Updates.releasesAPI.absoluteString.contains(AppInfo.repository))
        XCTAssertTrue(AppInfo.issuesURL.absoluteString.contains(AppInfo.repository))
        XCTAssertEqual(AppInfo.releaseNotesURL("0.2.0").lastPathComponent, "v0.2.0")
    }

    func testAFoundUpdateIsRememberedUntilItsInstalled() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "UpdatesTests.found"))
        defaults.removePersistentDomain(forName: "UpdatesTests.found")
        let page = URL(string: "https://github.com/TheIdeaPlace/MyPhotoLibraryDescribed/releases/tag/mac-v0.2.0")!
        Updates.remember(.init(version: "0.2.0", page: page), in: defaults)
        XCTAssertEqual(Updates.remembered(running: "0.1.1", in: defaults)?.version, "0.2.0")
        XCTAssertNil(Updates.remembered(running: "0.2.0", in: defaults), "installed: forgotten")
        Updates.remember(nil, in: defaults)
        XCTAssertNil(Updates.remembered(running: "0.1.1", in: defaults))
        defaults.removePersistentDomain(forName: "UpdatesTests.found")
    }
}
