import Foundation

/// The app's name and version, in one place. "My Photo Library Described" is the name for now (Kelly, 13 Oct; #34, #125): the display name is set once,
/// in project.yml (CFBundleDisplayName), and everything the app says or shows uses `AppInfo.name`. Identifiers that
/// are costly to change (the bundle ID) are kept separately and on purpose; the data folder is named after the app.
enum AppInfo {
    static var name: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String
            ?? Bundle.main.object(forInfoDictionaryKey: "CFBundleName") as? String
            ?? "My Photo Library Described"
    }

    /// The version people see, "0.1.0", which releases and update checks compare.
    static var shortVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    static var version: String {
        let short = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "?"
        return "\(short) (\(build))"
    }

    /// The GitHub repo, in one place: moving it to TheIdeaPlace for release (The-Idea-Place-Projects'
    /// MOVING-TO-THEIDEAPLACE.md), or renaming it, changes only this. Copies already installed keep working through
    /// GitHub's redirect from the old address.
    static let repository = "TheIdeaPlace/MyPhotoLibraryDescribed"

    /// The prefilled GitHub page Report a Bug opens when the relay can't be used (#33).
    static let issuesURL = URL(string: "https://github.com/\(repository)/issues/new")!

    /// The app kit's bug-report relay (#33), and this app's id there. The address is the repo variable
    /// APPKIT_RELAY_URL; the key is the secret APPKIT_RELAY_KEY, never in the repo.
    static let relayURL = URL(string: "https://appkit-bug-relay.quickmail.workers.dev/report")!
    static let relayAppID = "photofinder"

    /// The relay key built into this copy (Info.plist's AppKitRelayKey, from the APPKIT_RELAY_KEY build setting that
    /// scripts/release-mac.sh passes), or nil when it was built without one, as every build but a release is. Then
    /// Report a Bug opens the GitHub page. Never logged or put in a report.
    static var relayKey: String? { relayKey(from: Bundle.main.object(forInfoDictionaryKey: "AppKitRelayKey") as? String) }

    static func relayKey(from value: String?) -> String? {
        guard let key = value?.trimmingCharacters(in: .whitespacesAndNewlines), !key.isEmpty,
              !key.hasPrefix("$(") else { return nil }
        return key
    }

    /// Help > User Guide and Help > Privacy Policy: the documents in the repo, shared by the Mac and Windows apps (#105).
    static let userGuideURL = URL(string: "https://github.com/\(repository)/blob/main/docs/user-guide.md")!
    static let privacyPolicyURL = URL(string: "https://github.com/\(repository)/blob/main/docs/PRIVACY.md")!
    static let repositoryURL = URL(string: "https://github.com/\(repository)")!

    /// The release notes of a Mac version, for "See What's New".
    static func releaseNotesURL(_ version: String) -> URL {
        URL(string: "https://github.com/\(repository)/releases/tag/\(Updates.tagPrefix)\(version)")!
    }

    /// The folder in Application Support, named after the app so people can find it (Kelly, 13 Oct).
    static let dataFolderName = "My Photo Library Described"

    /// `~/Library/Application Support/My Photo Library Described`.
    static var dataFolder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(dataFolderName, isDirectory: true)
    }
}
