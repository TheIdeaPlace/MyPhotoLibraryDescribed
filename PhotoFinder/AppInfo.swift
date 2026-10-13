import Foundation

/// The app's name and version, in one place. "Photo Finder" is a working name (#34): the display name is set once,
/// in project.yml (CFBundleDisplayName), and everything the app says or shows uses `AppInfo.name`. Identifiers that
/// are costly to change (the bundle ID, the Application Support folder) are kept separately and on purpose.
enum AppInfo {
    static var name: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String
            ?? Bundle.main.object(forInfoDictionaryKey: "CFBundleName") as? String
            ?? "Photo Finder"
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
    static let repository = "kellylford/PhotoFinder"

    /// Where Report a Bug files issues, until the app kit's relay is set up for this app (#33).
    static let issuesURL = URL(string: "https://github.com/\(repository)/issues/new")!

    /// Help > User Guide and Help > Privacy Policy: the documents in the repo, shared by the Mac and Windows apps (#105).
    static let userGuideURL = URL(string: "https://github.com/\(repository)/blob/main/docs/user-guide.md")!
    static let privacyPolicyURL = URL(string: "https://github.com/\(repository)/blob/main/docs/PRIVACY.md")!

    /// The release notes of a Mac version, for "See What's New".
    static func releaseNotesURL(_ version: String) -> URL {
        URL(string: "https://github.com/\(repository)/releases/tag/\(Updates.tagPrefix)\(version)")!
    }

    /// The folder in Application Support. Not the display name: renaming the app mustn't lose saved descriptions.
    static let dataFolderName = "PhotoFinder"
}
