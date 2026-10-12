import XCTest
@testable import Photo_Finder

@MainActor
final class AutoDescriberTests: XCTestCase {
    private var spoken: [String] = []
    private var described: [String] = []
    private var current: String?
    private var savedText: [String: String] = [:]
    private var reason: String?
    /// Holds descriptions until released, to check what happens while one is running.
    private var gates: [CheckedContinuation<Void, Never>] = []
    private var holdDescriptions = false
    private let photos = ["a", "b", "c", "d", "e"]
    private var describedAhead: [String] = []
    private var aheadGates: [CheckedContinuation<Void, Never>] = []
    private var holdAhead = false
    private var aheadAllowed = true

    /// Moves one photo at a time, as arrowing does.
    private func step(_ describer: AutoDescriber, to id: String, by step: Int) {
        current = id
        describer.moved(to: id, step: step)
    }

    private func make(enabled: Bool = true) -> AutoDescriber {
        let describer = AutoDescriber(isEnabled: enabled, pause: .milliseconds(10))
        describer.saved = { [unowned self] in savedText[$0] }
        describer.unavailableReason = { [unowned self] in reason }
        describer.isStillOn = { [unowned self] in current == $0 }
        describer.speak = { [unowned self] in spoken.append($0) }
        describer.nextPhoto = { [unowned self] id, step in
            guard let i = photos.firstIndex(of: id), photos.indices.contains(i + step) else { return nil }
            let next = photos[i + step]
            return savedText[next] == nil ? next : nil
        }
        describer.describeAhead = { [unowned self] id in
            describedAhead.append(id)
            if holdAhead { await withCheckedContinuation { aheadGates.append($0) } }
            guard !Task.isCancelled else { return }
            savedText[id] = "Description of \(id)"
        }
        describer.mayDescribeAhead = { [unowned self] in aheadAllowed }
        describer.describe = { [unowned self] id in
            described.append(id)
            if holdDescriptions { await withCheckedContinuation { gates.append($0) } }
            savedText[id] = "Description of \(id)"
            return "Description of \(id)"
        }
        return describer
    }

    private func move(_ describer: AutoDescriber, to id: String?) {
        current = id
        describer.moved(to: id)
    }

    /// Waits (up to a second) for a description to be held at the gate.
    private func waitForGate() async {
        for _ in 0..<200 where gates.isEmpty { try? await Task.sleep(for: .milliseconds(5)) }
    }

    private func release(_ describer: AutoDescriber) async {
        holdDescriptions = false
        gates.forEach { $0.resume() }
        gates = []
        await describer.whenIdle()
    }

    func testDescribesAndSpeaksThePhotoYouSettleOn() async {
        let describer = make()
        move(describer, to: "a")
        await describer.whenIdle()
        XCTAssertEqual(described, ["a"])
        XCTAssertEqual(spoken, ["Description of a"])
    }

    func testArrowingPastPhotosDescribesOnlyTheLastOne() async {
        let describer = make()
        for id in ["a", "b", "c", "d"] { move(describer, to: id) }
        await describer.whenIdle()
        XCTAssertEqual(described, ["d"])
    }

    func testASavedDescriptionIsSpokenAfterThePauseWithoutDescribing() async {
        savedText["a"] = "Saved"
        let describer = make()
        move(describer, to: "a")
        XCTAssertEqual(spoken, [], "the row's own name is heard first")
        await describer.whenIdle()
        XCTAssertEqual(spoken, ["Saved"])
        XCTAssertEqual(described, [])
    }

    func testTheSameSelectionSetAgainIsNotMoving() async {
        savedText["a"] = "Saved"
        let describer = make()
        move(describer, to: "a")
        await describer.whenIdle()
        describer.moved(to: "a")  // the list refreshed
        await describer.whenIdle()
        XCTAssertEqual(spoken, ["Saved"])
    }

    func testOneFinishedAfterYouMovedOnIsNotSpokenAndTheNewPhotoIsDescribedNext() async {
        holdDescriptions = true
        let describer = make()
        move(describer, to: "a")
        await waitForGate()
        XCTAssertEqual(describer.describingID, "a")
        move(describer, to: "b")
        try? await Task.sleep(for: .milliseconds(40))  // b settles while a is still being described
        await release(describer)
        XCTAssertEqual(described, ["a", "b"])
        XCTAssertEqual(spoken, ["Description of b"])
    }

    func testDescribePhotoDuringThePauseIsNotDescribedTwice() async {
        let describer = make(enabled: true)
        move(describer, to: "a")
        describer.describingManually("a")  // Shift-Command-D before the pause ends
        await describer.whenIdle()
        XCTAssertEqual(described, [])
    }

    func testDescribePhotoOnAQueuedPhotoTakesItOutOfTheQueue() async {
        holdDescriptions = true
        let describer = make()
        move(describer, to: "a")
        await waitForGate()
        move(describer, to: "b")
        try? await Task.sleep(for: .milliseconds(40))  // b is queued behind a
        describer.describingManually("b")
        savedText["b"] = "Described by hand"
        await release(describer)
        XCTAssertEqual(described, ["a"])
    }

    func testLeavingDuringThePauseAndComingBackDescribesIt() async {
        let describer = make()
        current = "a"
        describer.moved(to: "a")
        current = nil  // Get Info opened before the pause ended
        await describer.whenIdle()
        XCTAssertEqual(described, [])
        current = "a"
        describer.refocused(currentID: "a")
        await describer.whenIdle()
        XCTAssertEqual(spoken, ["Description of a"])
        describer.refocused(currentID: "a")  // already spoken: not again
        await describer.whenIdle()
        XCTAssertEqual(spoken, ["Description of a"])
    }

    func testOffDoesNothing() async {
        let describer = make(enabled: false)
        move(describer, to: "a")
        await describer.whenIdle()
        XCTAssertEqual(described, [])
        XCTAssertEqual(spoken, [])
    }

    func testUnavailableIsSilentUnlessTurnedOn() async {
        reason = "No Apple Intelligence"
        let describer = make()
        move(describer, to: "a")
        await describer.whenIdle()
        XCTAssertEqual(spoken, [])
        describer.turnedOn(currentID: "a")
        XCTAssertEqual(spoken, ["No Apple Intelligence"])
        XCTAssertEqual(described, [])
    }

    func testTurningOnSaysSoAndDescribesThePhotoYouAreOn() async {
        let describer = make(enabled: false)
        move(describer, to: "a")
        describer.turnedOn(currentID: "a")
        await describer.whenIdle()
        XCTAssertEqual(spoken, ["Photos are described automatically", "Description of a"])
    }

    func testSteppingForwardDescribesTheNextPhotoAheadWithoutSpeakingIt() async {
        let describer = make()
        step(describer, to: "a", by: 1)
        await describer.whenIdle()
        XCTAssertEqual(spoken, ["Description of a"])
        XCTAssertEqual(describedAhead, ["b"])
        // Arriving on it: it's ready, so it's spoken without describing again.
        step(describer, to: "b", by: 1)
        await describer.whenIdle()
        XCTAssertEqual(described, ["a"], "b was described ahead, not again")
        XCTAssertEqual(spoken, ["Description of a", "Description of b"])
    }

    func testSteppingBackLooksBack() async {
        let describer = make()
        step(describer, to: "c", by: -1)
        await describer.whenIdle()
        XCTAssertEqual(describedAhead, ["b"])
    }

    func testAJumpOrArrowingQuicklyNeverLooksAhead() async {
        let describer = make()
        move(describer, to: "c")  // a jump: no step
        await describer.whenIdle()
        XCTAssertEqual(describedAhead, [])
        for id in ["a", "b", "c", "d"] { step(describer, to: id, by: 1) }  // arrowing past: only d settles
        await describer.whenIdle()
        XCTAssertEqual(describedAhead, ["e"], "only after settling")
    }

    func testLandingElsewhereDropsTheWorkAheadAtOnce() async {
        holdAhead = true
        let describer = make()
        step(describer, to: "a", by: 1)
        for _ in 0..<200 where aheadGates.isEmpty { try? await Task.sleep(for: .milliseconds(5)) }
        XCTAssertEqual(describer.aheadID, "b")
        move(describer, to: "d")  // jumped elsewhere
        XCTAssertNil(describer.aheadID)
        holdAhead = false
        aheadGates.forEach { $0.resume() }
        aheadGates = []
        await describer.whenIdle()
        XCTAssertNil(savedText["b"], "the dropped work isn't saved")
        XCTAssertEqual(spoken.last, "Description of d")
    }

    func testArrivingWhileItsStillBeingDescribedAheadWaitsForIt() async {
        holdAhead = true
        let describer = make()
        step(describer, to: "a", by: 1)
        for _ in 0..<200 where aheadGates.isEmpty { try? await Task.sleep(for: .milliseconds(5)) }
        XCTAssertEqual(describer.aheadID, "b")
        step(describer, to: "b", by: 1)  // arrives while b is still being described ahead
        try? await Task.sleep(for: .milliseconds(40))
        holdAhead = false
        aheadGates.forEach { $0.resume() }
        aheadGates = []
        await describer.whenIdle()
        XCTAssertFalse(described.contains("b"), "not described a second time")
        XCTAssertTrue(spoken.contains("Description of b"))
    }

    func testNotAheadWhenItIsntAGoodTime() async {
        aheadAllowed = false  // Low Power Mode, running hot, or in the background
        let describer = make()
        step(describer, to: "a", by: 1)
        await describer.whenIdle()
        XCTAssertEqual(describedAhead, [])
    }

    func testMovingAwayAndBackWhileItsDescribedAheadStillDescribesIt() async {
        holdAhead = true
        let describer = make()
        step(describer, to: "a", by: 1)
        for _ in 0..<200 where aheadGates.isEmpty { try? await Task.sleep(for: .milliseconds(5)) }
        step(describer, to: "b", by: 1)
        try? await Task.sleep(for: .milliseconds(40))  // settled on b: waiting for the work ahead
        step(describer, to: "c", by: 1)  // away, which drops it...
        step(describer, to: "b", by: -1)  // ...and straight back
        holdAhead = false
        aheadGates.forEach { $0.resume() }
        aheadGates = []
        await describer.whenIdle()
        XCTAssertTrue(spoken.contains("Description of b"), "b is described after all, not left silent")
    }

    func testAnOldDroppedTaskCantClearANewerOne() async {
        holdAhead = true
        let describer = make()
        step(describer, to: "a", by: 1)
        for _ in 0..<200 where aheadGates.isEmpty { try? await Task.sleep(for: .milliseconds(5)) }
        let first = aheadGates.removeFirst()  // the first look ahead at b, held
        move(describer, to: "e")              // dropped
        step(describer, to: "a", by: 1)       // back on a (described), looking ahead at b again
        for _ in 0..<200 where aheadGates.isEmpty { try? await Task.sleep(for: .milliseconds(5)) }
        XCTAssertEqual(describer.aheadID, "b")
        first.resume()                        // the old one finishes late
        try? await Task.sleep(for: .milliseconds(20))
        XCTAssertEqual(describer.aheadID, "b", "the newer work ahead is still known")
        holdAhead = false
        aheadGates.forEach { $0.resume() }
        aheadGates = []
        await describer.whenIdle()
    }
}
