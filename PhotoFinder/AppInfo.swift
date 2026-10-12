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

    static var version: String {
        let short = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "?"
        return "\(short) (\(build))"
    }

    /// Where Report a Bug files issues, until the app kit's relay is set up for this app (#33).
    static let issuesURL = URL(string: "https://github.com/kellylford/PhotoFinder/issues/new")!

    /// The folder in Application Support. Not the display name: renaming the app mustn't lose saved descriptions.
    static let dataFolderName = "PhotoFinder"
}
