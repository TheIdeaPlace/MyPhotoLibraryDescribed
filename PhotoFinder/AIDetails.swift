import Foundation

/// How describing has gone this session, for Check System (#94): how many descriptions Apple Intelligence made and how
/// long they took, and the last few failures. Kept in memory only, in Windows' wording.
@MainActor
final class DescribeStats {
    static let session = DescribeStats()

    private static let kept = 200
    private static let failuresKept = 3
    private var seconds: [Double] = []
    private var failures: [(when: Date, code: String, message: String)] = []
    private var count = 0
    private var failed = 0

    /// One photo described, counted once.
    func described(seconds took: Double) {
        count += 1
        seconds.append(took)
        if seconds.count > Self.kept { seconds.removeFirst() }
    }

    func failed(code: String, message: String, when: Date = Date()) {
        failed += 1
        failures.append((when, code, message))
        if failures.count > Self.failuresKept { failures.removeFirst() }
    }

    /// "12 descriptions, typically 3.1 seconds, slowest 9.8 seconds.", or nil before the first.
    func timings(locale: Locale = .current) -> String? {
        guard count > 0 else { return failed == 0 ? nil : "None succeeded; \(Self.count(failed, "failure", locale))." }
        let sorted = seconds.sorted()
        let middle = sorted.count / 2
        let median = sorted.count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2
        let text = "\(Self.count(count, "description", locale)), typically \(AIText.seconds(median, locale)), "
            + "slowest \(AIText.seconds(sorted[sorted.count - 1], locale))"
        return text + (failed > 0 ? "; \(Self.count(failed, "failure", locale))." : ".")
    }

    /// The last few failures, newest first: "18:30 refusal: Apple Intelligence declined to describe this image.".
    func recentFailures(locale: Locale = .current, timeZone: TimeZone = .current) -> [String] {
        let format = DateFormatter()
        format.locale = locale
        format.timeZone = timeZone
        format.dateStyle = .none
        format.timeStyle = .short
        return failures.reversed().map { failure in
            Privacy.redact("\(format.string(from: failure.when)) \(failure.code): \(AIText.sentence(failure.message))")
        }
    }

    private static func count(_ n: Int, _ what: String, _ locale: Locale) -> String {
        n == 1 ? "1 \(what)" : "\(n.formatted(.number.locale(locale))) \(what)s"
    }
}

/// What goes in something public, a bug report: no folder paths, which have the person's account name in them.
enum Privacy {
    static func redact(_ text: String, home: String = NSHomeDirectory()) -> String {
        var text = text
        if home.count > 1 { text = text.replacingOccurrences(of: home, with: "~") }
        // Another account's folder, or this one spelled differently: the name goes, the shape stays. A full stop after
        // it ends the sentence, not the name.
        return text.replacingOccurrences(of: #"/Users/[^/\s:;,]+?(?=\.?(?:[/\s:;,)"']|$))"#, with: "/Users/…", options: .regularExpression)
    }
}

enum AIText {
    /// "4.2 seconds", in the person's own number format.
    static func seconds(_ seconds: Double, _ locale: Locale = .current) -> String {
        String(format: "%.1f seconds", locale: locale, seconds)
    }

    /// A message as a sentence that ends with one full stop.
    static func sentence(_ text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let last = trimmed.last else { return "" }
        return ".!?".contains(last) ? trimmed : trimmed + "."
    }

    /// "the mug", "the mug and the table", "the mug, the table and the OPEN sign".
    static func list(_ things: [String]) -> String {
        switch things.count {
        case 0: ""
        case 1: things[0]
        default: things.dropLast().joined(separator: ", ") + " and " + things[things.count - 1]
        }
    }
}

/// The details Check System shows about this Mac (#94), for bug reports and for people checking their own setup. Each
/// is read without asking for anything, and left out when it can't be read.
enum SystemDetails {
    /// macOS's build, "27A5218g".
    static var osBuild: String? { sysctlString("kern.osversion") }

    /// The chip, "Apple M4 Pro".
    static var chip: String? { sysctlString("machdep.cpu.brand_string") }

    /// The Mac's memory in whole GB, "32 GB".
    static func memory(bytes: UInt64 = ProcessInfo.processInfo.physicalMemory) -> String? {
        bytes == 0 ? nil : "\(Int((Double(bytes) / 1_073_741_824).rounded())) GB"
    }

    /// Low Power Mode and how hot the Mac is running, both of which slow describing (and pause describing ahead).
    static func power(lowPower: Bool = ProcessInfo.processInfo.isLowPowerModeEnabled,
                      thermal: ProcessInfo.ThermalState = ProcessInfo.processInfo.thermalState) -> String {
        let heat = switch thermal {
        case .nominal: "temperature normal"
        case .fair: "running warm"
        case .serious: "running hot, so describing may be slower"
        case .critical: "running very hot, so describing will be slower"
        @unknown default: "temperature unknown"
        }
        return "Low Power Mode \(lowPower ? "on" : "off"), \(heat)"
    }

    private static func sysctlString(_ name: String) -> String? {
        var size = 0
        guard sysctlbyname(name, nil, &size, nil, 0) == 0, size > 1 else { return nil }
        var buffer = [CChar](repeating: 0, count: size)
        guard sysctlbyname(name, &buffer, &size, nil, 0) == 0 else { return nil }
        let text = String(cString: buffer).trimmingCharacters(in: .whitespaces)
        return text.isEmpty ? nil : text
    }
}

/// Help > Check AI Description (#95): describes a sample picture that comes with the app, to show describing works end
/// to end apart from the person's own photos, with a known result for bug reports. The sample is shared with Windows
/// (windows/src/PhotoFinder/Assets/SamplePhoto.jpg, drawn by windows/scripts/MakeSamplePhoto.cs): a red mug on a
/// wooden table beside a sign reading OPEN. Nothing is changed or saved: no photo is touched and the description isn't
/// kept. The same lines and wording as Windows' result, where the facts match.
struct AIDescriptionCheck: Equatable {
    /// What the sample shows, for the person and for a bug report.
    static let sampleShows = "A red mug on a wooden table beside a sign reading OPEN"

    /// Things a description of the sample should mention, each with the words that count. Only a hint: wording varies,
    /// so a miss is said, never counted as a failure.
    static let expected: [(thing: String, words: [String])] = [
        ("the mug", ["mug", "cup"]),
        ("the table", ["table", "desk"]),
        ("the OPEN sign", ["open"]),
    ]

    /// The description, or nil when it failed.
    let description: String?
    /// Seconds it took; zero when it wasn't tried.
    let took: Double
    let code: String?
    let message: String?
    /// Whether the prompt is the standard one or the person's own, from Settings.
    let standardPrompt: Bool
    /// The macOS the model came with, "macOS 27.1 (27A5218g)".
    let modelSystem: String
    let locale: Locale

    init(description: String?, took: Double, code: String? = nil, message: String? = nil, standardPrompt: Bool,
         modelSystem: String, locale: Locale = .current) {
        self.description = description
        self.took = took
        self.code = code
        self.message = message
        self.standardPrompt = standardPrompt
        self.modelSystem = modelSystem
        self.locale = locale
    }

    var worked: Bool { description != nil }

    /// What the sample shows that the description doesn't mention.
    var missing: [String] {
        guard let description else { return [] }
        return Self.expected.filter { !$0.words.contains { Self.mentions(description, $0) } }.map(\.thing)
    }

    var items: [SystemCheck.Item] {
        var items: [SystemCheck.Item] = []
        if let description {
            // First, so it's what focus lands on and what's read.
            items.append(.init(name: "Description", status: .info,
                               detail: description.isEmpty ? "Apple Intelligence returned an empty description." : description))
            let missing = self.missing
            items.append(missing.isEmpty
                ? .init(name: "Looks right", status: .ok, detail: "It mentions \(AIText.list(Self.expected.map(\.thing))).")
                : .init(name: "Looks right", status: .info, detail:
                    "It doesn't mention \(AIText.list(missing)), which the sample shows. Descriptions vary, so this isn't necessarily a problem."))
        } else {
            let reason = AIText.sentence(message ?? "Apple Intelligence couldn't describe it.")
            items.append(.init(name: "Result", status: .problem, detail: code.map { "\(reason) (\($0))" } ?? reason))
        }
        if took > 0 { items.append(.init(name: "Time", status: .info, detail: AIText.seconds(took, locale) + ".")) }
        items.append(.init(name: "Prompt", status: .info,
                           detail: standardPrompt ? "Standard. Change it in Settings." : "Your own, from Settings."))
        items.append(.init(name: "Model", status: .info,
                           detail: "Apple Intelligence's on-device model, from \(modelSystem)."))
        items.append(.init(name: "Sample photo", status: .info,
                           detail: "\(Self.sampleShows). Drawn for \(AppInfo.name); nothing on this Mac was changed or saved."))
        return items
    }

    var summary: String {
        worked ? "Apple Intelligence described the sample photo in \(AIText.seconds(took, locale))."
            // The reason is the first line, where focus lands, so it isn't said twice.
            : "Apple Intelligence couldn't describe the sample photo."
    }

    /// Everything, for Copy to Clipboard.
    var text: String { ([summary] + items.map(\.line)).joined(separator: "\n") }

    /// For a bug report, which is public: the same, without folder paths.
    var reportText: String { Privacy.redact(text) }

    /// One line for Check System and bug reports.
    var reportSummary: String {
        if worked {
            let missing = self.missing
            return "Worked in \(AIText.seconds(took, locale))"
                + (missing.isEmpty ? ", and mentions what the sample shows." : ", but doesn't mention \(AIText.list(missing)).")
        }
        let reason = AIText.sentence(message ?? "")
        let parts = [code, reason.isEmpty ? nil : reason].compactMap { $0 }
        return Privacy.redact(parts.isEmpty ? "Failed." : "Failed: " + parts.joined(separator: ": ") + (reason.isEmpty ? "." : ""))
    }

    /// The word on its own or as a plural ("mugs"), not inside another word ("reopened").
    static func mentions(_ text: String, _ word: String) -> Bool {
        let pattern = "\\b\(NSRegularExpression.escapedPattern(for: word))s?\\b"
        return text.range(of: pattern, options: [.regularExpression, .caseInsensitive]) != nil
    }
}
