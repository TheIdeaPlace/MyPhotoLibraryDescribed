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

    private func make(enabled: Bool = true) -> AutoDescriber {
        let describer = AutoDescriber(isEnabled: enabled, pause: .milliseconds(10))
        describer.saved = { [unowned self] in savedText[$0] }
        describer.unavailableReason = { [unowned self] in reason }
        describer.isStillOn = { [unowned self] in current == $0 }
        describer.speak = { [unowned self] in spoken.append($0) }
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
}
