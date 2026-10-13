import XCTest
@testable import Photo_Finder

/// Report a Bug through the app kit's relay (#33): the text and request it sends, when the GitHub page is used
/// instead, and the crash check. Nothing here touches the network.
final class BugReportTests: XCTestCase {
    private let relay = URL(string: "https://relay.example/report")!

    private func report() -> BugReport {
        var report = BugReport()
        report.title = "  Gallery skips a photo "
        report.happened = "Pressing Right Arrow skipped one."
        report.steps = "1. Open All Photos"
        report.systemText = "macOS: OK. macOS 27.1."
        return report
    }

    // MARK: The report

    func testTheBodyHasTheAnswersAndTheSystemCheck() {
        let body = report().body
        XCTAssertTrue(body.contains("**What happened**\nPressing Right Arrow skipped one."))
        XCTAssertTrue(body.contains("**Steps**\n1. Open All Photos"))
        XCTAssertTrue(body.contains("**System**\n```\nmacOS: OK. macOS 27.1.\n```"))
        // An unanswered question isn't sent as an empty heading.
        XCTAssertFalse(body.contains("What you expected"))
    }

    func testTheSystemCheckCanBeLeftOut() {
        var report = report()
        report.includeSystemCheck = false
        XCTAssertFalse(report.body.contains("macOS 27.1"))
    }

    func testASuggestionHasItsOneField() {
        var report = report()
        report.kind = .suggestion
        report.suggestion = "Sort by place."
        XCTAssertTrue(report.body.hasPrefix("**Suggestion**\nSort by place."))
        XCTAssertFalse(report.body.contains("What happened"))
    }

    func testTheTitleSaysItsFromTheMac() {
        XCTAssertEqual(report().fullTitle, "[Mac] Gallery skips a photo")
        XCTAssertEqual(BugReport().fullTitle, "[Mac] Bug report")
    }

    func testThePreviewShowsWhatIsSent() {
        var report = report()
        report.contact = " me@example.com "
        XCTAssertTrue(report.preview.hasPrefix("Title: [Mac] Gallery skips a photo\nKind: Bug\nContact: me@example.com\n\n"))
        XCTAssertTrue(report.preview.hasSuffix(report.body))
    }

    func testAttachmentsFollowTheSystemCheck() {
        var report = report()
        report.attachments = [.init(name: "the crash report", heading: "Crash report (first 60 lines)", text: "Process: My Photo Library Described"),
                              .init(name: "today's log", heading: "Log", text: "12:00 [start]")]
        XCTAssertTrue(report.body.contains("**Crash report (first 60 lines)**\n```\nProcess: My Photo Library Described\n```"))
        XCTAssertEqual(report.attachmentsToggleTitle, "Include the crash report and today's log")
        report.includeAttachments = false
        XCTAssertFalse(report.body.contains("Crash report"))
    }

    // MARK: The relay

    func testTheRequestCarriesTheAppAndKeyInHeaders() throws {
        var report = report()
        report.contact = "me@example.com"
        let request = BugReportRelay.request(for: report, key: "secret", url: relay, appID: "photofinder")
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertEqual(request.url, relay)
        XCTAssertEqual(request.timeoutInterval, 15)
        XCTAssertEqual(request.value(forHTTPHeaderField: "X-AppKit-App"), "photofinder")
        XCTAssertEqual(request.value(forHTTPHeaderField: "X-AppKit-Key"), "secret")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/json")
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: try XCTUnwrap(request.httpBody)) as? [String: String])
        XCTAssertEqual(json, ["title": "[Mac] Gallery skips a photo", "body": report.body, "kind": "bug",
                              "contact": "me@example.com"])
        // The key goes in its header only.
        XCTAssertFalse(String(decoding: request.httpBody!, as: UTF8.self).contains("secret"))
    }

    func testNoContactLeavesTheFieldOut() throws {
        var report = report()
        report.kind = .suggestion
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: report.relayJSON) as? [String: String])
        XCTAssertNil(json["contact"])
        XCTAssertEqual(json["kind"], "suggestion")
    }

    func testAFiledReportGivesTheIssue() async {
        let result = await BugReportRelay.send(report(), key: "k", url: relay) { request in
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (Data(#"{"issueUrl":"https://github.com/o/r/issues/42","number":42}"#.utf8), response)
        }
        XCTAssertEqual(result, .filed(number: 42, url: URL(string: "https://github.com/o/r/issues/42")!))
        XCTAssertFalse(BugReportRelay.needsBrowser(result))
    }

    func testARefusalOpensTheBrowser() async {
        let result = await BugReportRelay.send(report(), key: "k", url: relay) { request in
            (Data(#"{"error":"bad key"}"#.utf8), HTTPURLResponse(url: request.url!, statusCode: 401, httpVersion: nil, headerFields: nil)!)
        }
        XCTAssertEqual(result, .failed)
        XCTAssertTrue(BugReportRelay.needsBrowser(result))
    }

    func testANetworkErrorOpensTheBrowser() async {
        let result = await BugReportRelay.send(report(), key: "k", url: relay) { _ in throw URLError(.timedOut) }
        XCTAssertEqual(result, .failed)
    }

    func testAnUnexpectedAnswerOpensTheBrowser() async {
        let result = await BugReportRelay.send(report(), key: "k", url: relay) { request in
            (Data("<html>".utf8), HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
        }
        XCTAssertEqual(result, .failed)
    }

    func testNoKeySendsNothing() async {
        let sent = expectation(description: "sent")
        sent.isInverted = true
        let result = await BugReportRelay.send(report(), key: nil, url: relay) { _ in
            sent.fulfill()
            throw URLError(.cancelled)
        }
        await fulfillment(of: [sent], timeout: 0.1)
        XCTAssertEqual(result, .noKey)
        XCTAssertTrue(BugReportRelay.needsBrowser(result))
    }

    func testTheBuiltInKeyIsNilWhenEmptyOrUnset() {
        XCTAssertNil(AppInfo.relayKey(from: nil))
        XCTAssertNil(AppInfo.relayKey(from: ""))
        XCTAssertNil(AppInfo.relayKey(from: "  "))
        XCTAssertNil(AppInfo.relayKey(from: "$(APPKIT_RELAY_KEY)"))
        XCTAssertEqual(AppInfo.relayKey(from: "abc123\n"), "abc123")
    }

    func testTheBrowserPageIsPrefilledAndLabelled() throws {
        var report = report()
        report.kind = .suggestion
        let url = report.issuePageURL(issuesURL: URL(string: "https://github.com/o/r/issues/new")!)
        let items = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems)
        XCTAssertEqual(items.first { $0.name == "title" }?.value, "[Mac] Gallery skips a photo")
        XCTAssertEqual(items.first { $0.name == "labels" }?.value, "enhancement,user-reported,mac")
        XCTAssertTrue(items.first { $0.name == "body" }?.value?.contains("Open Log Folder") ?? false)
    }

    func testALongReportIsCutShortOnThePageOnly() throws {
        var report = report()
        let log = (1...100).map { "line \($0) " + String(repeating: "x", count: 60) }.joined(separator: "\n")
        report.attachments = [.init(name: "today's log", heading: "Log", text: log)]
        let url = report.issuePageURL(issuesURL: URL(string: "https://github.com/o/r/issues/new")!)
        let body = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "body" }?.value)
        XCTAssertLessThan(body.count, 5200)
        XCTAssertTrue(body.hasSuffix("paste the rest from the clipboard.)"))
        XCTAssertTrue(report.clipboardText.contains("line 100 "))
    }

    func testALongReportKeepsTheEndOfItsAttachmentsWithinTheRelaysLimit() {
        var report = report()
        let log = (1...200).map { "line \($0) " + String(repeating: "é", count: 60) }.joined(separator: "\n")
        report.attachments = [.init(name: "the crash report", heading: "Crash", text: "short summary"),
                              .init(name: "the log", heading: "Log", text: log)]
        let body = report.body
        XCTAssertLessThanOrEqual(body.utf16.count, BugReport.longestBody)
        XCTAssertTrue(body.contains("**Crash**\n```\nshort summary\n```"))
        XCTAssertTrue(body.contains("**Log**\n```\n(Earlier lines left out.)\nline "))
        XCTAssertTrue(body.hasSuffix("line 200 " + String(repeating: "é", count: 60) + "\n```"))
        XCTAssertFalse(body.contains("line 1 "))
        // The preview is exactly what's sent.
        XCTAssertTrue(report.preview.hasSuffix(body))
    }

    func testAShortReportIsUnchanged() {
        var report = report()
        report.attachments = [.init(name: "the log", heading: "Log", text: "one\ntwo")]
        XCTAssertTrue(report.body.hasSuffix("**Log**\n```\none\ntwo\n```"))
    }

    func testAnAttachmentWithNoRoomForALineIsLeftOutAndSaysSo() {
        var report = report()
        report.happened = String(repeating: "a", count: 7_700)
        report.attachments = [.init(name: "the log", heading: "Log", text: String(repeating: "x", count: 500))]
        XCTAssertTrue(report.body.hasSuffix("**Log**\n(Left out: the report is too long to include it.)"))
    }

    // MARK: Crashes

    private func temporaryFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: folder) }
        return folder
    }

    func testAQuitAfterACleanRunAsksNothing() throws {
        let folder = try temporaryFolder()
        XCTAssertNil(CrashCheck.begin(folder: folder))
        CrashCheck.end(folder: folder)
        XCTAssertNil(CrashCheck.begin(folder: folder))
    }

    func testAFlagLeftBehindMeansTheLastRunDidntQuitCleanly() throws {
        let folder = try temporaryFolder()
        let first = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertNil(CrashCheck.begin(folder: folder, now: first))
        // No end(): the app crashed.
        let previous = try XCTUnwrap(CrashCheck.begin(folder: folder, now: first.addingTimeInterval(3600)))
        XCTAssertEqual(previous.timeIntervalSince1970, first.timeIntervalSince1970, accuracy: 1)
        // Asked once: this run's flag is the new one.
        CrashCheck.end(folder: folder)
        XCTAssertNil(CrashCheck.begin(folder: folder))
    }

    /// #137's review: a second copy running at once isn't a crash of the first.
    func testAFlagFromACopyStillRunningIsNotACrash() throws {
        let folder = try temporaryFolder()
        XCTAssertNil(CrashCheck.begin(folder: folder, pid: 111))
        XCTAssertNil(CrashCheck.begin(folder: folder, isRunning: { $0 == 111 }, pid: 222))
        // That copy has gone without removing its flag (killed): now it counts.
        XCTAssertNotNil(CrashCheck.begin(folder: folder, isRunning: { _ in false }, pid: 333))
    }

    /// #35: a flag left by a Mac restarted with the app open isn't a crash.
    func testAFlagFromBeforeTheMacRestartedIsNotACrash() throws {
        let folder = try temporaryFolder()
        let first = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertNil(CrashCheck.begin(folder: folder, now: first, bootTime: first.addingTimeInterval(-60)))
        // The Mac restarted half an hour later; the app opens again after it.
        var asked: Date?
        XCTAssertNil(CrashCheck.begin(folder: folder, now: first.addingTimeInterval(3600),
                                      isRunning: { _ in false }, pid: 2,
                                      bootTime: first.addingTimeInterval(1800),
                                      crashReport: { asked = $0; return nil }))
        XCTAssertEqual(try XCTUnwrap(asked).timeIntervalSince1970, first.timeIntervalSince1970, accuracy: 1)
    }

    func testACrashReportBeforeTheMacRestartedIsStillOffered() throws {
        let folder = try temporaryFolder()
        let first = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertNil(CrashCheck.begin(folder: folder, now: first, bootTime: first.addingTimeInterval(-60)))
        let previous = try XCTUnwrap(CrashCheck.begin(folder: folder, now: first.addingTimeInterval(3600),
                                                      isRunning: { _ in false }, pid: 2,
                                                      bootTime: first.addingTimeInterval(1800),
                                                      crashReport: { _ in URL(fileURLWithPath: "/tmp/x.ips") }))
        XCTAssertEqual(previous.timeIntervalSince1970, first.timeIntervalSince1970, accuracy: 1)
    }

    func testAFlagFromSinceTheMacStartedIsACrash() throws {
        let folder = try temporaryFolder()
        let first = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertNil(CrashCheck.begin(folder: folder, now: first, bootTime: first.addingTimeInterval(-60)))
        // Same boot, no crash report (a force quit): offered, as before.
        XCTAssertNotNil(CrashCheck.begin(folder: folder, now: first.addingTimeInterval(3600),
                                         isRunning: { _ in false }, pid: 2,
                                         bootTime: first.addingTimeInterval(-60), crashReport: { _ in nil }))
        // A boot time that can't be read changes nothing either.
        XCTAssertNotNil(CrashCheck.begin(folder: folder, now: first.addingTimeInterval(7200),
                                         isRunning: { _ in false }, pid: 3, bootTime: nil, crashReport: { _ in nil }))
    }

    func testTheBootTimeIsReadAndInThePast() throws {
        let boot = try XCTUnwrap(CrashCheck.systemBootTime())
        XCTAssertLessThan(boot, Date())
        XCTAssertGreaterThan(boot, Date(timeIntervalSince1970: 1_600_000_000))
    }

    /// #137's review: only the useful part of a crash report, and nothing that identifies the Mac.
    func testACrashReportIsSummarisedWithoutIdentifiers() {
        let header = #"{"app_name":"My Photo Library Described","app_version":"0.2.0","build_version":"1","os_version":"macOS 27.2 (27C5)","incident_id":"ABC-123"}"#
        let body = #"{"crashReporterKey":"SECRET-KEY","sleepWakeUUID":"WAKE-UUID","userID":501,"exception":{"type":"EXC_BAD_ACCESS","signal":"SIGSEGV"},"termination":{"indicator":"Segmentation fault: 11"},"faultingThread":1,"usedImages":[{"name":"My Photo Library Described"},{"name":"SwiftUI"}],"threads":[{"frames":[]},{"frames":[{"imageIndex":0,"symbol":"LibraryModel.describe()"},{"imageIndex":1,"symbol":"ViewGraph.update"}]}]}"#
        let summary = CrashCheck.crashSummary(header + "\n" + body)
        XCTAssertTrue(summary.contains("App: My Photo Library Described 0.2.0 (1)"))
        XCTAssertTrue(summary.contains("macOS: macOS 27.2 (27C5)"))
        XCTAssertTrue(summary.contains("Exception: EXC_BAD_ACCESS, SIGSEGV"))
        XCTAssertTrue(summary.contains("0  My Photo Library Described  LibraryModel.describe()"))
        XCTAssertTrue(summary.contains("1  SwiftUI  ViewGraph.update"))
        for secret in ["SECRET-KEY", "WAKE-UUID", "ABC-123", "501"] { XCTAssertFalse(summary.contains(secret), secret) }
        XCTAssertEqual(CrashCheck.crashSummary("not a crash report"), "macOS wrote a crash report, but it couldn't be read here.")
    }

    func testTheNewestMatchingCrashReportSinceTheLastLaunch() throws {
        let folder = try temporaryFolder()
        let launch = Date(timeIntervalSince1970: 1_800_000_000)
        func write(_ name: String, _ date: Date) throws {
            let url = folder.appendingPathComponent(name)
            try Data("x".utf8).write(to: url)
            try FileManager.default.setAttributes([.modificationDate: date], ofItemAtPath: url.path)
        }
        try write("Photo App-2027-01-01-000000.ips", launch.addingTimeInterval(-60))   // before the launch
        try write("Photo App-2027-01-15-000000.ips", launch.addingTimeInterval(60))
        try write("Photo App-2027-01-15-000100.ips", launch.addingTimeInterval(120))   // the newest
        try write("Other App-2027-01-15-000200.ips", launch.addingTimeInterval(180))   // another app
        try write("Photo App-2027-01-15-000300.diag", launch.addingTimeInterval(240))  // not a crash report
        XCTAssertEqual(CrashCheck.newestCrashReport(in: folder, appName: "Photo App", since: launch)?.lastPathComponent,
                       "Photo App-2027-01-15-000100.ips")
        XCTAssertNil(CrashCheck.newestCrashReport(in: folder, appName: "Photo App", since: launch.addingTimeInterval(600)))
    }

    func testExcerptsKeepTheirEndAndLeaveOutFolders() {
        let text = (1...100).map { "line \($0) /Users/kelly/Pictures/a.jpg" }.joined(separator: "\n") + "\n"
        let first = CrashCheck.excerpt(text, first: 60, home: "/Users/kelly")
        XCTAssertEqual(first.components(separatedBy: "\n").count, 60)
        XCTAssertTrue(first.hasPrefix("line 1 ~/Pictures/a.jpg"))
        let last = CrashCheck.excerpt(text, last: 40, home: "/Users/kelly")
        XCTAssertEqual(last.components(separatedBy: "\n").count, 40)
        XCTAssertTrue(last.hasPrefix("line 61 "))
        XCTAssertTrue(last.hasSuffix("line 100 ~/Pictures/a.jpg"))
        // A very long line is cut.
        let long = CrashCheck.excerpt(String(repeating: "y", count: 4_000), home: "/Users/kelly")
        XCTAssertEqual(long, String(repeating: "y", count: CrashCheck.longestLine) + " …")
        // Another account's folder loses its name.
        XCTAssertEqual(CrashCheck.excerpt("at /Users/sam/x", home: "/Users/kelly"), "at /Users/…/x")
    }
}
