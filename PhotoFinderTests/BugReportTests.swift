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
        report.attachments = [.init(name: "today's log", heading: "Log", text: String(repeating: "x", count: 20_000))]
        let url = report.issuePageURL(issuesURL: URL(string: "https://github.com/o/r/issues/new")!)
        let body = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "body" }?.value)
        XCTAssertLessThan(body.count, 5200)
        XCTAssertTrue(body.hasSuffix("paste the rest from the clipboard.)"))
        XCTAssertTrue(report.clipboardText.contains(String(repeating: "x", count: 20_000)))
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
        // Another account's folder loses its name.
        XCTAssertEqual(CrashCheck.excerpt("at /Users/sam/x", home: "/Users/kelly"), "at /Users/…/x")
    }
}
