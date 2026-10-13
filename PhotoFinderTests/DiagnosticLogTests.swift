import XCTest
@testable import Photo_Finder

final class DiagnosticLogTests: XCTestCase {
    func testALineHasTheTimeTheAreaAndOneLineOfText() throws {
        let date = try XCTUnwrap(Calendar.current.date(from: DateComponents(year: 2026, month: 10, day: 12, hour: 9, minute: 5, second: 7)))
        let line = DiagnosticLog.line("save", "Couldn't write into\n/Users/kelly/Pictures/a.jpg", at: date, home: "/Users/kelly")
        XCTAssertEqual(line, "09:05:07.000 [save] Couldn't write into ~/Pictures/a.jpg")
    }

    func testOneFileADay() throws {
        let date = try XCTUnwrap(Calendar.current.date(from: DateComponents(year: 2026, month: 3, day: 4)))
        XCTAssertEqual(DiagnosticLog.fileName(for: date), "PhotoFinder-2026-03-04.log")
    }

    func testFilesAreInTheDataFolderAndKeptTwoWeeks() {
        XCTAssertEqual(DiagnosticLog.folder.lastPathComponent, "Logs")
        XCTAssertEqual(DiagnosticLog.folder.deletingLastPathComponent().lastPathComponent, AppInfo.dataFolderName)
        XCTAssertEqual(DiagnosticLog.daysKept, 14)
    }
}
