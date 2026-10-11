import XCTest
@testable import Photo_Finder

@MainActor
final class AutoDescriberTests: XCTestCase {
    private var spoken: [String] = []
    private var described: [String] = []
    private var current: String?
    private var savedText: [String: String] = [:]
    private var reason: String?
    /// Holds a description until the test releases it, to check what happens while one is running.
    private var gate: CheckedContinuation<Void, Never>?
    private var holdDescriptions = false

    private func make(enabled: Bool = true) -> AutoDescriber {
        let describer = AutoDescriber(isEnabled: enabled, pause: .milliseconds(20))
        describer.saved = { [unowned self] in savedText[$0] }
        describer.unavailableReason = { [unowned self] in reason }
        describer.isStillOn = { [unowned self] in current == $0 }
        describer.speak = { [unowned self] in spoken.append($0) }
        describer.describe = { [unowned self] id in
            described.append(id)
            if holdDescriptions { await withCheckedContinuation { gate = $0 } }
            return "Description of \(id)"
        }
        return describer
    }

    private func move(_ describer: AutoDescriber, to id: String?) {
        current = id
        describer.moved(to: id)
    }

    private func settle() async { try? await Task.sleep(for: .milliseconds(80)) }

    func testDescribesAndSpeaksThePhotoYouSettleOn() async {
        let describer = make()
        move(describer, to: "a")
        await settle()
        XCTAssertEqual(described, ["a"])
        XCTAssertEqual(spoken, ["Description of a"])
    }

    func testArrowingPastPhotosDescribesOnlyTheLastOne() async {
        let describer = make()
        for id in ["a", "b", "c", "d"] { move(describer, to: id) }
        await settle()
        XCTAssertEqual(described, ["d"])
    }

    func testASavedDescriptionIsSpokenAfterThePauseWithoutDescribing() async {
        savedText["a"] = "Saved"
        let describer = make()
        move(describer, to: "a")
        XCTAssertEqual(spoken, [], "the row's own name is heard first")
        await settle()
        XCTAssertEqual(spoken, ["Saved"])
        XCTAssertEqual(described, [])
    }

    func testTheSameSelectionSetAgainIsNotMoving() async {
        savedText["a"] = "Saved"
        let describer = make()
        move(describer, to: "a")
        await settle()
        describer.moved(to: "a")  // the list refreshed
        await settle()
        XCTAssertEqual(spoken, ["Saved"])
    }

    func testOneFinishedAfterYouMovedOnIsNotSpokenAndTheNewPhotoIsDescribedNext() async {
        holdDescriptions = true
        let describer = make()
        move(describer, to: "a")
        await settle()
        XCTAssertEqual(describer.describingID, "a")
        move(describer, to: "b")
        await settle()
        holdDescriptions = false
        gate?.resume()
        await settle()
        XCTAssertEqual(described, ["a", "b"])
        XCTAssertEqual(spoken, ["Description of b"])
    }

    func testOffDoesNothing() async {
        let describer = make(enabled: false)
        move(describer, to: "a")
        await settle()
        XCTAssertEqual(described, [])
        XCTAssertEqual(spoken, [])
    }

    func testUnavailableIsSilentUnlessTurnedOn() async {
        reason = "No Apple Intelligence"
        let describer = make()
        move(describer, to: "a")
        await settle()
        XCTAssertEqual(spoken, [])
        describer.turnedOn(currentID: "a")
        XCTAssertEqual(spoken, ["No Apple Intelligence"])
        XCTAssertEqual(described, [])
    }

    func testTurningOnDescribesThePhotoYouAreOn() async {
        let describer = make(enabled: false)
        move(describer, to: "a")
        describer.turnedOn(currentID: "a")
        await settle()
        XCTAssertEqual(spoken, ["Description of a"])
    }
}
