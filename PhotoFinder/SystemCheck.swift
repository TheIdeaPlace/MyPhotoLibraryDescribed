import AppKit
import Foundation
import Photos

/// Help > Check System: whether this Mac can do what the app is for, describing photos, and what to do if not.
/// One line per check, in words VoiceOver reads well, with the same layout as Windows' Check System (#32).
struct SystemCheck: Equatable {
    struct Item: Equatable, Identifiable {
        enum Status: Equatable { case ok, problem, info }
        let name: String
        let status: Status
        let detail: String
        /// The name, made unique where a name repeats ("Recent failure"), so the list can select each line.
        var id: String

        init(name: String, status: Status, detail: String) {
            self.name = name
            self.status = status
            self.detail = detail
            self.id = name
        }

        /// The same lines with unique ids: a repeated name gets " 2", " 3" and so on.
        static func identified(_ items: [Item]) -> [Item] {
            var seen: [String: Int] = [:]
            return items.map { item in
                var item = item
                seen[item.name, default: 0] += 1
                if let n = seen[item.name], n > 1 { item.id = "\(item.name) \(n)" }
                return item
            }
        }

        /// "Apple Intelligence: OK. Ready to describe photos." as one line, for the list and the clipboard.
        var line: String {
            let word = switch status {
            case .ok: "OK"
            case .problem: "Needs attention"
            case .info: ""
            }
            return word.isEmpty ? "\(name): \(detail)" : "\(name): \(word). \(detail)"
        }
    }

    let items: [Item]

    var summary: String {
        let problems = items.filter { $0.status == .problem }.count
        return problems == 0 ? "Everything needed to describe photos is ready."
            : problems == 1 ? "1 thing needs attention." : "\(problems) things need attention."
    }

    /// Everything, for Copy to Clipboard.
    var text: String { ([summary] + items.map(\.line)).joined(separator: "\n") }

    /// For a bug report, which is public: the same, without folder paths.
    var reportText: String { Privacy.redact(text) }

    /// The facts the checks are made from, gathered separately so the wording can be tested.
    struct Facts {
        var appName: String
        var appVersion: String
        var osVersion: OperatingSystemVersion
        var appleSilicon: Bool
        /// Nil when Apple Intelligence can describe; otherwise why not.
        var aiUnavailableReason: String?
        var photosAccess: PHAuthorizationStatus
        var savedDescriptions: Int
        var describeAutomatically: Bool
        var describeInBackground: Bool

        // The details (#94), after the checks: each is left out when it couldn't be read.
        /// macOS's build, "27A5218g".
        var osBuild: String? = nil
        /// "Apple M4 Pro".
        var chip: String? = nil
        var memory: String? = nil
        /// Low Power Mode and the thermal state.
        var power: String? = nil
        /// Apple Intelligence's availability as the frameworks report it: "Available", or the reason it isn't.
        var aiModelState: String? = nil
        /// The language descriptions are asked in, and whether Apple Intelligence supports it.
        var aiLanguage: String? = nil
        /// This session's descriptions: how many, and how long they took.
        var describeTimings: String? = nil
        var recentFailures: [String] = []
        /// Help > Check AI Description's last result this session (#95).
        var aiDescriptionCheck: String? = nil
    }

    /// "macOS 27.1 (27A5218g)", for the macOS line and Check AI Description's model.
    static func osText(_ os: OperatingSystemVersion, build: String?) -> String {
        "macOS \(os.majorVersion).\(os.minorVersion)" + (os.patchVersion > 0 ? ".\(os.patchVersion)" : "")
            + (build.map { " (\($0))" } ?? "")
    }

    init(_ facts: Facts) {
        let os = facts.osVersion
        let osText = Self.osText(os, build: facts.osBuild)
        var items: [Item] = [
            Item(name: facts.appName, status: .info, detail: "Version \(facts.appVersion)."),
            os.majorVersion >= 27
                ? Item(name: "macOS", status: .ok, detail: "\(osText).")
                : Item(name: "macOS", status: .problem,
                       detail: "\(osText). Describing photos needs macOS 27 or later. Browsing works on this version."),
            facts.appleSilicon
                ? Item(name: "Processor", status: .ok, detail: facts.chip.map { "\($0), Apple silicon." } ?? "Apple silicon.")
                : Item(name: "Processor", status: .problem,
                       detail: (facts.chip.map { "\($0). " } ?? "Intel. ") + "Apple Intelligence needs a Mac with Apple silicon."),
        ]
        if os.majorVersion < 27 {
            // Already said in the macOS line: one cause, one problem.
            items.append(Item(name: "Apple Intelligence", status: .info, detail: "Not checked: describing needs macOS 27."))
        } else if let reason = facts.aiUnavailableReason {
            items.append(Item(name: "Apple Intelligence", status: .problem, detail: reason))
        } else {
            items.append(Item(name: "Apple Intelligence", status: .ok, detail: "Ready to describe photos, on this Mac."))
        }
        switch facts.photosAccess {
        case .authorized:
            items.append(Item(name: "Photos library", status: .ok, detail: "Full access."))
        case .limited:
            items.append(Item(name: "Photos library", status: .problem, detail:
                "Limited to the photos you chose. To see them all: System Settings, Privacy & Security, Photos."))
        case .restricted:
            items.append(Item(name: "Photos library", status: .problem, detail:
                "Blocked by Screen Time or your organization's device management. Ask whoever manages this Mac."))
        case .notDetermined:
            items.append(Item(name: "Photos library", status: .problem, detail:
                "Not asked yet. \(facts.appName) asks when it opens the library."))
        default:
            items.append(Item(name: "Photos library", status: .problem, detail:
                "No access. Turn it on in System Settings, Privacy & Security, Photos."))
        }
        let saved = facts.savedDescriptions
        items.append(Item(name: "Saved descriptions", status: .info, detail:
            saved == 0 ? "None yet." : saved == 1 ? "1 photo described." : "\(saved.formatted()) photos described."))
        items.append(Item(name: "Describe Photos Automatically", status: .info,
                          detail: facts.describeAutomatically ? "On." : "Off. Turn it on in the Photo menu."))
        items.append(Item(name: "Describe New Photos in the Background", status: .info,
                          detail: facts.describeInBackground ? "On." : "Off."))
        // The details last, so the checks that might need doing something come first. The order is Windows' where the
        // facts match.
        Self.addDetails(&items, facts)
        self.items = Item.identified(items)
    }

    /// The Mac, and what's behind Apple Intelligence, and how describing has gone this session.
    private static func addDetails(_ items: inout [Item], _ facts: Facts) {
        if let memory = facts.memory { items.append(Item(name: "Memory", status: .info, detail: "\(memory).")) }
        if let power = facts.power { items.append(Item(name: "Power", status: .info, detail: "\(power).")) }
        if let state = facts.aiModelState {
            items.append(Item(name: "Apple Intelligence model", status: .info, detail: "\(state)."))
        }
        if let language = facts.aiLanguage {
            items.append(Item(name: "Apple Intelligence language", status: .info, detail: "\(language)."))
        }
        if let timings = facts.describeTimings {
            items.append(Item(name: "Descriptions this session", status: .info, detail: timings))
        }
        for failure in facts.recentFailures { items.append(Item(name: "Recent failure", status: .info, detail: failure)) }
        if let check = facts.aiDescriptionCheck {
            items.append(Item(name: "AI description check", status: .info, detail: check))
        }
    }
}

extension SystemCheck {
    /// The body of a new bug report: the questions the app kit's Report a Bug form asks, then the system check.
    static func bugReportTemplate(systemText: String) -> String {
        """
        **What happened**


        **What you expected**


        **Steps**
        1.

        **System**
        ```
        \(systemText)
        ```
        """
    }
}
