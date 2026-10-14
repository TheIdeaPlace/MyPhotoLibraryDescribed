import XCTest
@testable import Photo_Finder

/// The data folder is named after the app, so people can find it (Kelly, 13 Oct).
final class DataFolderTests: XCTestCase {
    func testTheFolderIsNamedAfterTheApp() {
        XCTAssertEqual(AppInfo.dataFolderName, AppInfo.name)
        XCTAssertEqual(AppInfo.dataFolder.lastPathComponent, "My Photo Library Described")
        XCTAssertEqual(AppInfo.dataFolder.deletingLastPathComponent().lastPathComponent, "Application Support")
    }
}
