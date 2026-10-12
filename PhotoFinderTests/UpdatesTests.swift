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
         "html_url": "https://github.com/kellylford/PhotoFinder/releases/tag/\(tag)"]
    }

    func testTheNewestMacReleaseIsFoundAndOthersAreIgnored() {
        let list = [release("mac-v0.2.0"), release("mac-v0.3.0", draft: true), release("mac-v0.4.0", prerelease: true),
                    release("v0.9.0"), release("mac-v0.2.5"), release("mac-v0.1.0")]
        XCTAssertEqual(Updates.newer(than: "0.1.0", in: list)?.version, "0.2.5")
        XCTAssertNil(Updates.newer(than: "0.2.5", in: list), "up to date")
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
        XCTAssertEqual(AppInfo.releaseNotesURL("0.1.1").lastPathComponent, "mac-v0.1.1")
    }
}
