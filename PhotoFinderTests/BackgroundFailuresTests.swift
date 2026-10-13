import XCTest
@testable import Photo_Finder

/// Photos background describing couldn't describe, remembered across launches until the next version (#163, as
/// Windows' BackgroundFailures.json since #162). Each test has a temporary folder of its own; the real Application
/// Support folder is never touched.
final class BackgroundFailuresTests: XCTestCase {
    private func makeFolder() -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("BackgroundFailuresTests-\(UUID().uuidString)")
        addTeardownBlock {
            try? FileManager.default.removeItem(at: folder)
            DispatchQueue.global().asyncAfter(deadline: .now() + 3) { try? FileManager.default.removeItem(at: folder) }
        }
        return folder
    }

    private func failuresURL(in folder: URL) -> URL { folder.appendingPathComponent(BackgroundFailures.fileName) }

    func testAPhotoThatFailedIsRememberedAfterARestart() {
        let url = failuresURL(in: makeFolder())
        let before = BackgroundFailures(fileURL: url, version: "1.0 (1)")
        XCTAssertFalse(before.contains("photo-1", stamp: "5"))
        before.insert("photo-1", stamp: "5")
        before.insert("file:/USERS/K/A.JPG|10|20")
        XCTAssertTrue(before.contains("photo-1", stamp: "5"))
        before.save()

        let after = BackgroundFailures(fileURL: url, version: "1.0 (1)")
        XCTAssertEqual(after.count, 2)
        XCTAssertTrue(after.contains("photo-1", stamp: "5"))
        XCTAssertTrue(after.contains("file:/USERS/K/A.JPG|10|20"))
        XCTAssertFalse(after.contains("photo-2", stamp: "5"))
    }

    func testAnEditedPhotoIsTriedAgain() {
        // A Photos library photo edited since has a new modification date, so a new stamp.
        let failures = BackgroundFailures(fileURL: failuresURL(in: makeFolder()), version: "1")
        let date = Date(timeIntervalSince1970: 1_700_000_000)
        failures.insert("photo-1", stamp: BackgroundFailures.stamp(modified: date))
        XCTAssertTrue(failures.contains("photo-1", stamp: BackgroundFailures.stamp(modified: date)))
        XCTAssertFalse(failures.contains("photo-1", stamp: BackgroundFailures.stamp(modified: date.addingTimeInterval(60))))
        XCTAssertFalse(failures.contains("photo-1", stamp: BackgroundFailures.stamp(modified: nil)))
        // Failing again under the new date replaces the old entry rather than adding a second.
        failures.insert("photo-1", stamp: BackgroundFailures.stamp(modified: date.addingTimeInterval(60)))
        XCTAssertEqual(failures.count, 1)
        // An edited file has a new key (size and date are in it).
        let file = FileDescriptionKey.key(path: "/Users/k/a.jpg", size: 10, modified: date)
        failures.insert(file)
        XCTAssertFalse(failures.contains(FileDescriptionKey.key(path: "/Users/k/a.jpg", size: 11, modified: date)))
    }

    func testANewVersionTriesThemAllAgain() {
        let url = failuresURL(in: makeFolder())
        let old = BackgroundFailures(fileURL: url, version: "0.2.0 (1)")
        old.insert("photo-1")
        old.save()
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path))

        let updated = BackgroundFailures(fileURL: url, version: "0.2.1 (1)")
        XCTAssertEqual(updated.count, 0)
        XCTAssertFalse(updated.contains("photo-1"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path), "the old version's list is cleared")
    }

    func testTheListIsKeptSmall_TheOldestGoFirst() {
        let url = failuresURL(in: makeFolder())
        let failures = BackgroundFailures(fileURL: url, version: "1", limit: 3)
        for i in 1...5 { failures.insert("photo-\(i)") }
        XCTAssertEqual(failures.count, 3)
        XCTAssertFalse(failures.contains("photo-1"))
        XCTAssertFalse(failures.contains("photo-2"))
        XCTAssertTrue(failures.contains("photo-5"))
        failures.save()
        XCTAssertEqual(BackgroundFailures(fileURL: url, version: "1", limit: 3).count, 3)
    }

    func testAFileThatCantBeReadIsAnEmptyList() throws {
        let folder = makeFolder()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data("not json".utf8).write(to: failuresURL(in: folder))
        let failures = BackgroundFailures(fileURL: failuresURL(in: folder), version: "1")
        XCTAssertEqual(failures.count, 0)
        failures.insert("photo-1")
        failures.save()
        XCTAssertTrue(BackgroundFailures(fileURL: failuresURL(in: folder), version: "1").contains("photo-1"))
    }

    func testRemoveTakesAPhotoOffTheList() {
        let url = failuresURL(in: makeFolder())
        let failures = BackgroundFailures(fileURL: url, version: "1")
        failures.insert("photo-1", stamp: "5")
        failures.insert("photo-2")
        failures.remove("photo-1")
        XCTAssertFalse(failures.contains("photo-1", stamp: "5"))
        failures.save()
        XCTAssertFalse(BackgroundFailures(fileURL: url, version: "1").contains("photo-1", stamp: "5"))
        XCTAssertTrue(BackgroundFailures(fileURL: url, version: "1").contains("photo-2"))
    }

    func testADescriptionSavedByAnyRouteTakesThePhotoOffTheList() {
        // Describe Photo (Command-D) still describes a photo on the list; once it's described, it's off the list.
        let folder = makeFolder()
        let cache = DescriptionCache(fileURL: folder.appendingPathComponent("Descriptions.json"))
        let failures = BackgroundFailures(fileURL: failuresURL(in: folder), version: "1", cache: cache)
        failures.insert("photo-1", stamp: "5")
        failures.insert("photo-2", stamp: "5")
        let changed = expectation(forNotification: DescriptionCache.didChange, object: cache)
        cache.remember("A cat on a mat.", forKey: "photo-1")
        wait(for: [changed], timeout: 2)
        XCTAssertFalse(failures.contains("photo-1", stamp: "5"))
        XCTAssertTrue(failures.contains("photo-2", stamp: "5"))
    }

    func testSavesAreBatched_NotWrittenOnEveryFailure() {
        let url = failuresURL(in: makeFolder())
        let failures = BackgroundFailures(fileURL: url, version: "1")
        failures.insert("photo-1")
        failures.insert("photo-2")
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path), "written a couple of seconds later, once")
        let written = expectation(description: "saved")
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { written.fulfill() }
        wait(for: [written], timeout: 5)
        XCTAssertEqual(BackgroundFailures(fileURL: url, version: "1").count, 2)
    }

    // MARK: - Which failures are remembered (#163's review)

    private let first = BackgroundFailure(key: "photo-1", stamp: "5")
    private let second = BackgroundFailure(key: "file:/USERS/K/B.JPG|10|20")

    func testAFailureAboutThePhotoIsRememberedAtOnce() {
        // Apple Intelligence's guardrails declined it, or it couldn't be read.
        var run = BackgroundFailures.Run()
        XCTAssertEqual(run.failed(first, .aboutThePhoto), [first])
        XCTAssertEqual(run.described(), [])
    }

    func testOtherFailuresAreRememberedOnlyOnceALaterPhotoIsDescribed() {
        // A timeout or the model service failing may be the Mac, not the photo; a later photo described shows it wasn't.
        var run = BackgroundFailures.Run()
        XCTAssertEqual(run.failed(first, .other), [])
        XCTAssertEqual(run.failed(second, .other), [])
        XCTAssertEqual(run.described(), [first, second])
        XCTAssertEqual(run.described(), [], "remembered once")
    }

    func testOtherFailuresAreForgottenWhenTheRunStops() {
        // Five in a row, or Stop Background Describing: none of them are remembered, so a broken setup doesn't leave
        // good photos skipped until the next update.
        var run = BackgroundFailures.Run()
        for _ in 0..<5 { XCTAssertEqual(run.failed(first, .other), []) }
        run.ended()
        XCTAssertEqual(run.described(), [])
    }

    func testACancelledDescriptionIsNeverRemembered() {
        var run = BackgroundFailures.Run()
        XCTAssertEqual(run.failed(first, .cancelled), [])
        XCTAssertEqual(run.described(), [])
    }

    func testItLivesBesideTheDescriptions() {
        let folder = makeFolder()
        let cache = DescriptionCache(fileURL: folder.appendingPathComponent("Descriptions.json"))
        let failures = BackgroundFailures(beside: cache)
        failures.insert("photo-1")
        failures.save()
        XCTAssertTrue(FileManager.default.fileExists(atPath: failuresURL(in: folder).path))
    }
}
