import Foundation

/// Help > Report a Bug… (#33): what the form collects, the text it sends, and how it's sent. The app kit's relay (a
/// Cloudflare Worker run by The Idea Place) files it as a GitHub issue with a bot account, so nobody needs a GitHub
/// account to report a bug. When the relay can't be reached, or this copy was built without a relay key, the
/// prefilled GitHub issue page opens instead, as before, with the whole report on the clipboard. Kept free of AppKit
/// and the network, so it can be tested; the form is BugReportView.
struct BugReport: Equatable {
    enum Kind: String, CaseIterable, Identifiable {
        case bug, suggestion
        var id: String { rawValue }
        var title: String { self == .bug ? "Bug" : "Suggestion" }
        /// GitHub's labels, as the relay applies them; the fallback page asks for the same (only people who can triage
        /// the repo get them, hence "[Mac]" in the title too).
        var labels: String { self == .bug ? "bug,user-reported,mac" : "enhancement,user-reported,mac" }
    }

    /// Every report's title starts with this, so the Mac's reports can be told from Windows' at a glance.
    static let titlePrefix = "[Mac] "

    var kind: Kind = .bug
    var title = ""
    var happened = ""
    var expected = ""
    var steps = ""
    /// A suggestion's one field.
    var suggestion = ""
    /// Optional, and public like everything else in the report.
    var contact = ""
    var includeSystemCheck = true
    /// Check System's report text, already without folder paths.
    var systemText = ""
    /// What came with the report from elsewhere: Check AI Description's result, or the last crash. Redacted already.
    var attachments: [Attachment] = []
    var includeAttachments = true

    struct Attachment: Equatable {
        /// What it's called in the form's toggle, "the crash report".
        var name: String
        var heading: String
        var text: String
    }

    /// The toggle for what came with the report: "Include the crash report and today's log".
    var attachmentsToggleTitle: String { "Include " + AIText.list(attachments.map(\.name)) }

    var fullTitle: String {
        let title = title.trimmingCharacters(in: .whitespacesAndNewlines)
        return Self.titlePrefix + (title.isEmpty ? (kind == .bug ? "Bug report" : "Suggestion") : title)
    }

    var trimmedContact: String? {
        let contact = contact.trimmingCharacters(in: .whitespacesAndNewlines)
        return contact.isEmpty ? nil : contact
    }

    /// The issue's text, laid out as Report a Bug's page always was: the questions, then the system check, then
    /// anything attached. An empty answer is left out rather than sent as an empty heading.
    var body: String {
        var sections: [String] = []
        func add(_ heading: String, _ text: String) {
            let text = text.trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty { sections.append("**\(heading)**\n\(text)") }
        }
        switch kind {
        case .bug:
            add("What happened", happened)
            add("What you expected", expected)
            add("Steps", steps)
        case .suggestion:
            add("Suggestion", suggestion)
        }
        if includeSystemCheck, !systemText.isEmpty { sections.append("**System**\n```\n\(systemText)\n```") }
        for attachment in attachments where includeAttachments && !attachment.text.isEmpty {
            sections.append("**\(attachment.heading)**\n```\n\(attachment.text)\n```")
        }
        return sections.isEmpty ? "(No details given.)" : sections.joined(separator: "\n\n")
    }

    /// Exactly what Send sends, for the form's preview: the title, the kind, the contact if there is one, and the body.
    var preview: String {
        var lines = ["Title: \(fullTitle)", "Kind: \(kind.title)"]
        if let contact = trimmedContact { lines.append("Contact: \(contact)") }
        return lines.joined(separator: "\n") + "\n\n" + body
    }

    /// The whole report, for the clipboard when the browser page is used instead.
    var clipboardText: String { fullTitle + "\n\n" + body }

    /// The relay's JSON: `{title, body, contact?, kind}`.
    var relayJSON: Data {
        var fields: [String: String] = ["title": fullTitle, "body": body, "kind": kind.rawValue]
        if let contact = trimmedContact { fields["contact"] = contact }
        return (try? JSONSerialization.data(withJSONObject: fields, options: [.sortedKeys])) ?? Data()
    }

    /// The prefilled GitHub page used when the relay isn't. GitHub refuses very long addresses, so the body is cut
    /// there; the whole report is on the clipboard.
    func issuePageURL(issuesURL: URL = AppInfo.issuesURL) -> URL {
        var body = body + "\n\n" + Self.logNote
        if body.count > Self.longestPageBody {
            body = String(body.prefix(Self.longestPageBody)) + "\n\n(Cut short here: paste the rest from the clipboard.)"
        }
        var components = URLComponents(url: issuesURL, resolvingAgainstBaseURL: false)!
        components.queryItems = [URLQueryItem(name: "title", value: fullTitle), URLQueryItem(name: "body", value: body),
                                 URLQueryItem(name: "labels", value: kind.labels)]
        return components.url ?? issuesURL
    }

    /// On the GitHub page only, where a file can be attached; the relay takes text alone.
    static let logNote = "Help > Open Log Folder has a log of what went wrong, which can be attached here. It names files and folders, so look at it first: issues are public."

    /// Characters of body the GitHub page is given, which keeps its address well under GitHub's limit once encoded.
    static let longestPageBody = 5000
}

/// Sending a report to the app kit's relay. The network is a closure, so tests send nothing.
enum BugReportRelay {
    typealias Transport = @Sendable (URLRequest) async throws -> (Data, URLResponse)

    /// The relay gives up at 12 seconds; the apps at 15 (the relay's README).
    static let timeout: TimeInterval = 15

    enum Result: Equatable {
        /// Filed: the issue's number and address.
        case filed(number: Int, url: URL)
        /// This copy has no relay key, so the browser page is used.
        case noKey
        /// The relay refused or couldn't be reached; the browser page is used.
        case failed
    }

    /// The POST to the relay, with the app's id and key. The key goes in a header and nowhere else.
    static func request(for report: BugReport, key: String, url: URL = AppInfo.relayURL,
                        appID: String = AppInfo.relayAppID) -> URLRequest {
        var request = URLRequest(url: url, timeoutInterval: timeout)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(appID, forHTTPHeaderField: "X-AppKit-App")
        request.setValue(key, forHTTPHeaderField: "X-AppKit-Key")
        request.httpBody = report.relayJSON
        return request
    }

    /// The relay's answer, `{issueUrl, number}`, or nil for anything else.
    static func filed(_ data: Data, _ response: URLResponse) -> Result? {
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let number = json["number"] as? Int, number > 0,
              let address = json["issueUrl"] as? String, let url = URL(string: address),
              url.scheme == "https" else { return nil }
        return .filed(number: number, url: url)
    }

    static func send(_ report: BugReport, key: String?, url: URL = AppInfo.relayURL,
                     transport: Transport = { try await URLSession.shared.data(for: $0) }) async -> Result {
        guard let key, !key.isEmpty else { return .noKey }
        do {
            let (data, response) = try await transport(request(for: report, key: key, url: url))
            if let filed = filed(data, response) { return filed }
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            DiagnosticLog.write("report", "The bug-report relay answered \(status); opening the GitHub page instead")
        } catch {
            DiagnosticLog.write("report", "The bug-report relay couldn't be reached (\((error as NSError).code)); opening the GitHub page instead")
        }
        return .failed
    }

    /// Whether the GitHub page has to be opened instead.
    static func needsBrowser(_ result: Result) -> Bool {
        if case .filed = result { return false }
        return true
    }
}

/// Whether the app quit cleanly last time (#33), as the app kit asks: a flag file in the data folder, written at
/// launch and removed when the app quits normally. If it's still there at the next launch, the app crashed, was
/// force-quit, or the Mac lost power, and the app offers once to report it.
enum CrashCheck {
    static var dataFolder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(AppInfo.dataFolderName, isDirectory: true)
    }

    static func flagURL(in folder: URL) -> URL { folder.appendingPathComponent("Running.flag") }

    /// Called at launch: when the last run didn't quit cleanly, when it started (the flag's date), otherwise nil.
    /// Then writes the flag for this run.
    @discardableResult
    static func begin(folder: URL = dataFolder, now: Date = Date()) -> Date? {
        let flag = flagURL(in: folder)
        let fm = FileManager.default
        var previous: Date?
        if fm.fileExists(atPath: flag.path) {
            previous = (try? fm.attributesOfItem(atPath: flag.path)[.modificationDate] as? Date) ?? .distantPast
        }
        try? fm.createDirectory(at: folder, withIntermediateDirectories: true)
        try? Data().write(to: flag)
        try? fm.setAttributes([.modificationDate: now], ofItemAtPath: flag.path)
        return previous
    }

    /// Called as the app quits normally.
    static func end(folder: URL = dataFolder) {
        try? FileManager.default.removeItem(at: flagURL(in: folder))
    }

    /// Set at launch when the last run didn't quit cleanly: when it started.
    nonisolated(unsafe) static var uncleanLaunch: Date?

    static var crashReportsFolder: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/DiagnosticReports", isDirectory: true)
    }

    /// macOS's newest crash report for this app (`<name>-<date>.ips`) written since `since`, if any.
    static func newestCrashReport(in folder: URL = crashReportsFolder, appName: String = AppInfo.name, since: Date) -> URL? {
        let keys: [URLResourceKey] = [.contentModificationDateKey]
        guard let files = try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys) else { return nil }
        return files
            .filter { $0.lastPathComponent.hasPrefix(appName + "-") && $0.pathExtension == "ips" }
            .compactMap { file -> (URL, Date)? in
                guard let date = (try? file.resourceValues(forKeys: Set(keys)))?.contentModificationDate, date > since else { return nil }
                return (file, date)
            }
            .max { $0.1 < $1.1 }?.0
    }

    /// The first lines of a text, or the last, without folder paths: a crash report or the log is public once sent.
    static func excerpt(_ text: String, first: Int? = nil, last: Int? = nil, home: String = NSHomeDirectory()) -> String {
        var lines = text.components(separatedBy: .newlines)
        while lines.last?.isEmpty == true { lines.removeLast() }
        if let first { lines = Array(lines.prefix(first)) }
        if let last { lines = Array(lines.suffix(last)) }
        return Privacy.redact(lines.joined(separator: "\n"), home: home)
    }

    /// What goes with a crash report: the start of macOS's crash report, and the end of today's diagnostic log.
    static func attachments(since: Date) -> [BugReport.Attachment] {
        var attachments: [BugReport.Attachment] = []
        if let report = newestCrashReport(since: since),
           let text = try? String(contentsOf: report, encoding: .utf8) {
            attachments.append(.init(name: "the crash report", heading: "Crash report (first 60 lines)", text: excerpt(text, first: 60)))
        }
        let log = DiagnosticLog.folder.appendingPathComponent(DiagnosticLog.fileName(for: Date()))
        if let text = try? String(contentsOf: log, encoding: .utf8) {
            attachments.append(.init(name: "today's log", heading: "Diagnostic log (last 40 lines)", text: excerpt(text, last: 40)))
        }
        return attachments
    }
}
