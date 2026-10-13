import Photos
import XCTest
@testable import Photo_Finder

/// Check System's details (#94) and Check AI Description (#95).
@MainActor
final class AIDetailsTests: XCTestCase {
    private let us = Locale(identifier: "en_US")
    private let utc = TimeZone(identifier: "UTC")!

    // MARK: - Description timings

    func testNoTimingsBeforeTheFirstDescription() {
        XCTAssertNil(DescribeStats().timings(locale: us))
    }

    func testTimingsGiveTheCountTheMedianAndTheSlowest() {
        let stats = DescribeStats()
        for seconds in [3.0, 9.84, 2.0] { stats.described(seconds: seconds) }
        XCTAssertEqual(stats.timings(locale: us), "3 descriptions, typically 3.0 seconds, slowest 9.8 seconds.")
        stats.described(seconds: 4.0)
        XCTAssertEqual(stats.timings(locale: us), "4 descriptions, typically 3.5 seconds, slowest 9.8 seconds.",
                       "an even count takes the middle two")
    }

    func testOneDescriptionIsSingularAndFailuresAreCounted() {
        let stats = DescribeStats()
        stats.described(seconds: 1.25)
        stats.failed(code: "refusal", message: "Apple Intelligence declined to describe this image.")
        XCTAssertEqual(stats.timings(locale: us), "1 description, typically 1.2 seconds, slowest 1.2 seconds; 1 failure.")
    }

    func testOnlyFailures() {
        let stats = DescribeStats()
        stats.failed(code: "a", message: "x")
        stats.failed(code: "b", message: "y")
        XCTAssertEqual(stats.timings(locale: us), "None succeeded; 2 failures.")
    }

    func testTheLastThreeFailuresNewestFirst() {
        let stats = DescribeStats()
        let start = Date(timeIntervalSince1970: 18 * 3600)  // 18:00 UTC
        for minute in 0..<4 {
            stats.failed(code: "code\(minute)", message: "Failure \(minute)", when: start.addingTimeInterval(Double(minute) * 60))
        }
        let failures = stats.recentFailures(locale: us, timeZone: utc)
        XCTAssertEqual(failures.count, 3)
        XCTAssertTrue(failures[0].hasSuffix("code3: Failure 3."), failures[0])
        XCTAssertTrue(failures[0].hasPrefix("6:03"), failures[0])
        XCTAssertTrue(failures[2].hasSuffix("code1: Failure 1."), failures[2])
    }

    func testAFailureNamingAFolderLeavesTheAccountOut() {
        let stats = DescribeStats()
        stats.failed(code: "NSCocoaErrorDomain 260", message: "Couldn't open /Users/someone/Pictures/a.jpg")
        XCTAssertTrue(stats.recentFailures(locale: us).first?.contains("/Users/…/Pictures/a.jpg") ?? false)
        XCTAssertFalse(stats.recentFailures(locale: us).first?.contains("someone") ?? true)
    }

    // MARK: - Redaction

    func testRedactionReplacesTheHomeFolder() {
        XCTAssertEqual(Privacy.redact("At /Users/kelly/Library/x.", home: "/Users/kelly"), "At ~/Library/x.")
        // Another account whose name starts with this one's isn't half replaced.
        XCTAssertEqual(Privacy.redact("At /Users/kellyford/x", home: "/Users/kelly"), "At /Users/…/x")
    }

    func testRedactionRemovesOtherAccountNames() {
        XCTAssertEqual(Privacy.redact("At /Users/pat/x and /Users/sam.", home: "/Users/kelly"), "At /Users/…/x and /Users/….")
    }

    func testRedactionLeavesOrdinaryTextAlone() {
        let text = "Apple Intelligence: OK. Ready to describe photos, on this Mac."
        XCTAssertEqual(Privacy.redact(text, home: "/Users/kelly"), text)
    }

    // MARK: - Details

    func testMemoryInWholeGigabytes() {
        XCTAssertEqual(SystemDetails.memory(bytes: 34_359_738_368), "32 GB")
        XCTAssertNil(SystemDetails.memory(bytes: 0))
    }

    func testPowerSaysLowPowerModeAndHeat() {
        XCTAssertEqual(SystemDetails.power(lowPower: false, thermal: .nominal), "Low Power Mode off, temperature normal")
        XCTAssertEqual(SystemDetails.power(lowPower: true, thermal: .serious),
                       "Low Power Mode on, running hot, so describing may be slower")
    }

    private func facts() -> SystemCheck.Facts {
        var facts = SystemCheck.Facts(
            appName: "My Photo Library Described", appVersion: "0.1.1 (1)",
            osVersion: OperatingSystemVersion(majorVersion: 27, minorVersion: 1, patchVersion: 0),
            appleSilicon: true, aiUnavailableReason: nil, photosAccess: .authorized, savedDescriptions: 3,
            describeAutomatically: true, describeInBackground: false)
        facts.osBuild = "27B42"
        facts.chip = "Apple M4 Pro"
        facts.memory = "32 GB"
        facts.power = "Low Power Mode off, temperature normal"
        facts.aiModelState = "Available"
        facts.aiLanguage = "English (United States), supported"
        facts.describeTimings = "2 descriptions, typically 3.0 seconds, slowest 4.0 seconds."
        facts.recentFailures = ["6:03 PM refusal: Declined.", "6:01 PM refusal: Declined."]
        facts.aiDescriptionCheck = "Worked in 4.2 seconds, and mentions what the sample shows."
        return facts
    }

    func testTheDetailsComeAfterTheChecksOneLineEach() {
        let check = SystemCheck(facts())
        let lines = check.items.map(\.line)
        XCTAssertTrue(lines.contains("macOS: OK. macOS 27.1 (27B42)."))
        XCTAssertTrue(lines.contains("Processor: OK. Apple M4 Pro, Apple silicon."))
        let details = Array(lines.drop { !$0.hasPrefix("Memory:") })
        XCTAssertEqual(details, [
            "Memory: 32 GB.",
            "Power: Low Power Mode off, temperature normal.",
            "Apple Intelligence model: Available.",
            "Apple Intelligence language: English (United States), supported.",
            "Descriptions this session: 2 descriptions, typically 3.0 seconds, slowest 4.0 seconds.",
            "Recent failure: 6:03 PM refusal: Declined.",
            "Recent failure: 6:01 PM refusal: Declined.",
            "AI description check: Worked in 4.2 seconds, and mentions what the sample shows.",
        ])
        XCTAssertEqual(check.summary, "Everything needed to describe photos is ready.", "details are never problems")
    }

    func testRepeatedLinesCanEachBeSelected() {
        let ids = SystemCheck(facts()).items.map(\.id)
        XCTAssertEqual(Set(ids).count, ids.count)
    }

    func testDetailsThatCouldNotBeReadAreLeftOut() {
        let check = SystemCheck(.init(
            appName: "My Photo Library Described", appVersion: "0.1.1 (1)",
            osVersion: OperatingSystemVersion(majorVersion: 27, minorVersion: 1, patchVersion: 0),
            appleSilicon: true, aiUnavailableReason: nil, photosAccess: .authorized, savedDescriptions: 0,
            describeAutomatically: false, describeInBackground: false))
        XCTAssertFalse(check.text.contains("Memory"))
        XCTAssertTrue(check.text.contains("macOS: OK. macOS 27.1."))
    }

    func testTheReportLeavesFoldersOut() {
        var facts = facts()
        facts.recentFailures = ["6:03 PM x: Couldn't read \(NSHomeDirectory())/Pictures/a.jpg."]
        let check = SystemCheck(facts)
        XCTAssertTrue(check.text.contains(NSHomeDirectory()))
        XCTAssertFalse(check.reportText.contains(NSHomeDirectory()))
        XCTAssertTrue(check.reportText.contains("~/Pictures/a.jpg"))
    }

    // MARK: - Check AI Description

    private func result(_ description: String?, took: Double = 4.24, code: String? = nil, message: String? = nil,
                        standard: Bool = true) -> AIDescriptionCheck {
        AIDescriptionCheck(description: description, took: took, code: code, message: message, standardPrompt: standard,
                           modelSystem: "macOS 27.1 (27B42)", locale: us)
    }

    func testTheDescriptionComesFirstThenWhetherItLooksRight() {
        let check = result("A red mug sits on a wooden table next to a chalkboard sign that reads OPEN.")
        XCTAssertEqual(check.items.map(\.line), [
            "Description: A red mug sits on a wooden table next to a chalkboard sign that reads OPEN.",
            "Looks right: OK. It mentions the mug, the table and the OPEN sign.",
            "Time: 4.2 seconds.",
            "Prompt: Standard. Change it in Settings.",
            "Model: Apple Intelligence's on-device model, from macOS 27.1 (27B42).",
            "Sample photo: A red mug on a wooden table beside a sign reading OPEN. Drawn for My Photo Library Described; nothing on this Mac was changed or saved.",
        ])
        XCTAssertEqual(check.summary, "Apple Intelligence described the sample photo in 4.2 seconds.")
        XCTAssertEqual(check.reportSummary, "Worked in 4.2 seconds, and mentions what the sample shows.")
        XCTAssertTrue(check.text.hasPrefix(check.summary + "\nDescription: A red mug"))
    }

    func testAMissingThingIsSaidButIsntAProblem() {
        let check = result("Two cups on a desk.")
        XCTAssertEqual(check.missing, ["the OPEN sign"])
        let line = check.items[1]
        XCTAssertEqual(line.status, .info)
        XCTAssertEqual(line.line, "Looks right: It doesn't mention the OPEN sign, which the sample shows. Descriptions vary, so this isn't necessarily a problem.")
        XCTAssertEqual(check.reportSummary, "Worked in 4.2 seconds, but doesn't mention the OPEN sign.")
    }

    func testWordsCountOnlyOnTheirOwn() {
        XCTAssertTrue(AIDescriptionCheck.mentions("Two MUGS.", "mug"))
        XCTAssertTrue(AIDescriptionCheck.mentions("a sign: Open", "open"))
        XCTAssertFalse(AIDescriptionCheck.mentions("The shop reopened.", "open"))
        XCTAssertFalse(AIDescriptionCheck.mentions("A timetable.", "table"))
        XCTAssertEqual(result("A coffee shop reopened beside a timetable.").missing, ["the mug", "the table", "the OPEN sign"])
    }

    /// As on Windows (#122): Apple Intelligence said "a wooden surface" for the table.
    func testATableCanBeASurfaceOrCounter() {
        XCTAssertEqual(result("A red mug on a wooden surface beside a sign reading OPEN.").missing, [])
        XCTAssertEqual(result("A red mug on a countertop beside a sign reading OPEN.").missing, [])
    }

    func testAFailureSaysWhyWithItsCode() {
        let check = result(nil, took: 1.5, code: "refusal", message: "Apple Intelligence declined to describe this image.")
        XCTAssertFalse(check.worked)
        XCTAssertEqual(check.items.first?.line,
                       "Result: Needs attention. Apple Intelligence declined to describe this image. (refusal)")
        XCTAssertEqual(check.summary, "Apple Intelligence couldn't describe the sample photo.")
        XCTAssertEqual(check.reportSummary, "Failed: refusal: Apple Intelligence declined to describe this image.")
        XCTAssertTrue(check.text.contains("Time: 1.5 seconds."))
    }

    func testUnavailableIsntTriedSoHasNoTime() {
        let check = result(nil, took: 0, code: "unavailable", message: "Turn on Apple Intelligence in System Settings to describe images.")
        XCTAssertFalse(check.text.contains("Time:"))
        XCTAssertEqual(check.items.first?.line,
                       "Result: Needs attention. Turn on Apple Intelligence in System Settings to describe images. (unavailable)")
    }

    func testYourOwnPromptIsSaid() {
        XCTAssertTrue(result("A mug.", standard: false).text.contains("Prompt: Your own, from Settings."))
    }

    func testTheReportOfAFailureLeavesFoldersOut() {
        let check = result(nil, code: "x", message: "Couldn't read \(NSHomeDirectory())/Library/y")
        XCTAssertFalse(check.reportText.contains(NSHomeDirectory()))
        XCTAssertFalse(check.reportSummary.contains(NSHomeDirectory()))
    }

    func testTheSampleComesWithTheApp() {
        XCTAssertNotNil(Bundle.main.url(forResource: "SamplePhoto", withExtension: "jpg"))
    }

    func testListsReadNaturally() {
        XCTAssertEqual(AIText.list(["a"]), "a")
        XCTAssertEqual(AIText.list(["a", "b"]), "a and b")
        XCTAssertEqual(AIText.list(["a", "b", "c"]), "a, b and c")
        XCTAssertEqual(AIText.sentence("Done"), "Done.")
        XCTAssertEqual(AIText.sentence("Done."), "Done.")
    }
}
