import XCTest
@testable import Photo_Finder

/// The data folder named after the app (Kelly, 13 Oct), with the folder from the old name moved in. Temporary folders
/// only: the person's own folder is never touched by tests.
final class DataFolderTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("DataFolderTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    private func folder(_ name: String) -> URL { root.appendingPathComponent(name, isDirectory: true) }

    func testTheFolderIsNamedAfterTheApp() {
        XCTAssertEqual(AppInfo.dataFolderName, AppInfo.name)
        XCTAssertEqual(AppInfo.dataFolder.lastPathComponent, "My Photo Library Described")
    }

    func testTheOldFolderIsMovedWithEverythingInIt() throws {
        let old = folder("PhotoFinder"), new = folder("My Photo Library Described")
        try FileManager.default.createDirectory(at: old.appendingPathComponent("Logs"), withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: old.appendingPathComponent("Descriptions.json"))
        XCTAssertEqual(DataFolder.moveEarlier([old], to: new), old)
        XCTAssertFalse(FileManager.default.fileExists(atPath: old.path))
        XCTAssertEqual(try Data(contentsOf: new.appendingPathComponent("Descriptions.json")), Data("{}".utf8))
        XCTAssertTrue(FileManager.default.fileExists(atPath: new.appendingPathComponent("Logs").path))
    }

    func testANewFolderIsNeverReplaced() throws {
        let old = folder("PhotoFinder"), new = folder("My Photo Library Described")
        try FileManager.default.createDirectory(at: old, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: new, withIntermediateDirectories: true)
        try Data("new".utf8).write(to: new.appendingPathComponent("Descriptions.json"))
        XCTAssertNil(DataFolder.moveEarlier([old], to: new))
        XCTAssertEqual(try Data(contentsOf: new.appendingPathComponent("Descriptions.json")), Data("new".utf8))
        XCTAssertTrue(FileManager.default.fileExists(atPath: old.path), "left where it is")
    }

    func testNothingToMoveDoesNothing() {
        XCTAssertNil(DataFolder.moveEarlier([folder("PhotoFinder")], to: folder("My Photo Library Described")))
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder("My Photo Library Described").path))
    }
}
