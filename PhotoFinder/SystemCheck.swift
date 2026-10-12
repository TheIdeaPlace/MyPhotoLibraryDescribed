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
        var id: String { name }

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

    /// Everything, for Copy to Clipboard and Report a Bug.
    var text: String { ([summary] + items.map(\.line)).joined(separator: "\n") }

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
    }

    init(_ facts: Facts) {
        let os = facts.osVersion
        let osText = "macOS \(os.majorVersion).\(os.minorVersion)" + (os.patchVersion > 0 ? ".\(os.patchVersion)" : "")
        var items: [Item] = [
            Item(name: facts.appName, status: .info, detail: "Version \(facts.appVersion)."),
            os.majorVersion >= 27
                ? Item(name: "macOS", status: .ok, detail: "\(osText).")
                : Item(name: "macOS", status: .problem,
                       detail: "\(osText). Describing photos needs macOS 27 or later. Browsing works on this version."),
            facts.appleSilicon
                ? Item(name: "Processor", status: .ok, detail: "Apple silicon.")
                : Item(name: "Processor", status: .problem,
                       detail: "Intel. Apple Intelligence needs a Mac with Apple silicon."),
        ]
        if let reason = facts.aiUnavailableReason {
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
        self.items = items
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
