import XCTest
@testable import Photo_Finder

/// More of describing in the background's bookkeeping (#21), alongside BackgroundQueueTests: what it says in the
/// status bar and when it finishes, and how photos join and leave the queue.
final class BackgroundQueueMoreTests: XCTestCase {
    func testAnEmptyQueueHasNothingToDescribeOrSay() {
        var queue = BackgroundQueue()
        XCTAssertTrue(queue.isEmpty)
        XCTAssertNil(queue.next())
        XCTAssertNil(queue.progress)
        XCTAssertNil(queue.summary)
        XCTAssertEqual(queue.add([]), 0)
        XCTAssertNil(queue.progress, "adding nothing doesn't start a run")
    }

    func testTheSamePhotoTwiceInOneAddIsQueuedOnce() {
        var queue = BackgroundQueue()
        XCTAssertEqual(queue.add(["a", "a", "b"]), 2)
        XCTAssertEqual(queue.pending, ["a", "b"])
        XCTAssertEqual(queue.total, 2)
    }

    func testAPhotoAlreadyTakenOffCanBeQueuedAgainAndCountsAgain() {
        var queue = BackgroundQueue()
        queue.add(["a"])
        XCTAssertEqual(queue.next(), "a")
        XCTAssertEqual(queue.add(["a"]), 1, "it changed since, so it's described again")
        XCTAssertEqual(queue.total, 2)
    }

    func testPuttingBackAPhotoThatsStillWaitingDoesntQueueItTwice() {
        var queue = BackgroundQueue()
        queue.add(["a", "b"])
        queue.putBack("b")
        XCTAssertEqual(queue.pending, ["a", "b"])
    }

    func testProgressNeverGoesPastTheTotal() {
        var queue = BackgroundQueue()
        queue.add(["a", "b"])
        _ = queue.next()
        queue.finished(described: true)
        _ = queue.next()
        queue.finished(described: true)
        XCTAssertEqual(queue.progress, "Describing photos in the background, 2 of 2", "not 3 of 2 once the last is done")
    }

    func testFailuresAndPhotosOnlyInICloudCountTowardTheProgress() {
        var queue = BackgroundQueue()
        queue.add(["a", "b", "c"])
        _ = queue.next()
        _ = queue.failed()
        _ = queue.next()
        queue.skippedNotOnThisMac()
        XCTAssertEqual(queue.done, 2)
        XCTAssertEqual(queue.progress, "Describing photos in the background, 3 of 3")
    }

    func testTheProgressCountsInThousands() {
        var queue = BackgroundQueue()
        queue.add((0..<1_500).map { "photo-\($0)" })
        XCTAssertEqual(queue.progress, "Describing photos in the background, 1 of \(1_500.formatted())")
    }

    func testTheSummaryCountsSeveralPhotos() {
        var queue = BackgroundQueue()
        queue.add(["a", "b", "c", "d", "e"])
        for _ in 0..<3 {
            _ = queue.next()
            queue.finished(described: true)
        }
        XCTAssertEqual(queue.summary, "Finished describing 3 photos in the background.")
        for _ in 0..<2 {
            _ = queue.next()
            queue.skippedNotOnThisMac()
        }
        XCTAssertEqual(queue.summary,
                       "Finished describing 3 photos in the background. 2 photos weren't described because they're only in iCloud.")
    }

    func testNothingIsSaidWhenNoPhotoWasDescribed() {
        // Every photo already described, or failed: nothing worth saying.
        var queue = BackgroundQueue()
        queue.add(["a", "b"])
        _ = queue.next()
        queue.finished(described: false)
        _ = queue.next()
        _ = queue.failed()
        XCTAssertNil(queue.summary)
    }

    func testOnlyPhotosInICloudIsStillSaid() {
        var queue = BackgroundQueue()
        queue.add(["a"])
        _ = queue.next()
        queue.skippedNotOnThisMac()
        let summary = queue.summary
        XCTAssertTrue(summary?.contains("1 photo wasn't described because it's only in iCloud.") == true, "\(summary ?? "nil")")
        // Found by #21: it started "Finished describing 0 photos in the background."
        XCTAssertEqual(summary, "Background describing finished. 1 photo wasn't described because it's only in iCloud.")
    }

    func testAPhotoNotDescribedNeitherResetsNorAddsToTheFailuresInARow() {
        var queue = BackgroundQueue()
        queue.add(["a", "b", "c"])
        _ = queue.next()
        _ = queue.failed()
        _ = queue.next()
        queue.finished(described: false)
        XCTAssertEqual(queue.failedInARow, 1)
        XCTAssertEqual(queue.described, 0)
    }

    func testTheStoppedMessageCountsOnePhotoLeft() {
        // Found by #21: "so 1 photo weren't tried". Spoken and shown in the status bar.
        XCTAssertEqual(BackgroundQueue.stoppedMessage(left: 1, reason: "Apple Intelligence isn't ready."),
                       "Background describing stopped: 5 photos in a row couldn't be described, so 1 photo wasn't tried. Apple Intelligence isn't ready.")
    }

    func testTheStoppedMessageWithNothingLeftSaysNothingAboutIt() {
        XCTAssertEqual(BackgroundQueue.stoppedMessage(left: 0, reason: "Apple Intelligence isn't ready."),
                       "Background describing stopped: 5 photos in a row couldn't be described. Apple Intelligence isn't ready.")
    }

    func testResetStartsAFreshRun() {
        var queue = BackgroundQueue()
        queue.add(["a", "b"])
        _ = queue.next()
        queue.finished(described: true)
        queue.reset()
        XCTAssertTrue(queue.isEmpty)
        XCTAssertEqual(queue.total, 0)
        XCTAssertEqual(queue.done, 0)
        XCTAssertNil(queue.summary)
        XCTAssertEqual(queue.add(["a"]), 1, "a photo from the last run can be queued again")
    }

    func testPhotosAreCountedInWords() {
        XCTAssertEqual(BackgroundQueue.plural(0), "0 photos")
        XCTAssertEqual(BackgroundQueue.plural(1), "1 photo")
        XCTAssertEqual(BackgroundQueue.plural(2), "2 photos")
        XCTAssertEqual(BackgroundQueue.plural(12_345), "\(12_345.formatted()) photos")
    }
}
