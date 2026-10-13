import AppKit
import Foundation

/// Help > Check for Updates (#22, #33): releases are GitHub releases tagged `v<version>`, one tag for both apps (Kelly,
/// 13 Oct), with the Mac's signed, notarized disk image; the Mac's earlier ones were tagged `mac-v<version>`. The app looks for a newer one quietly once a day and, if there is one, the menu item
/// says so ("Update to 0.2.0…"), without taking focus or interrupting, as the app kit asks. Choosing it opens the
/// release to download. No updater framework: a person drags the new app into Applications, as with most Mac apps.
enum Updates {
    static let tagPrefix = "v"
    /// The Mac's own tags before both apps shared one (0.1.x), still read so nothing is missed.
    static let earlierTagPrefix = "mac-v"

    /// The version a release's tag names, or nil for a tag that isn't a release's.
    static func version(fromTag tag: String) -> String? {
        for prefix in [earlierTagPrefix, tagPrefix] where tag.hasPrefix(prefix) {
            let version = String(tag.dropFirst(prefix.count))
            return version.first?.isNumber == true ? version : nil
        }
        return nil
    }
    static var releasesAPI: URL { URL(string: "https://api.github.com/repos/\(AppInfo.repository)/releases?per_page=30")! }

    /// The Help menu's item, which always says what's running, as the apps' shared update experience asks
    /// (The-Idea-Place-Projects#12).
    static func menuTitle(running: String, available: String?) -> String {
        available.map { "Update to \($0) available…" } ?? "Check for Updates (running \(running))…"
    }

    /// Whether to say "updated" on this launch: only when a version ran here before and this one is newer. Never on
    /// the first run or after going back to an older version.
    static func justUpdated(last: String?, current: String) -> Bool {
        guard let last else { return false }
        return isNewer(current, than: last)
    }

    struct Release: Equatable {
        let version: String
        let page: URL
    }

    /// The newest Mac release that's newer than `current`, from GitHub's list of releases (drafts and pre-releases
    /// left out), or nil.
    static func newer(than current: String, in releases: [[String: Any]]) -> Release? {
        releases.compactMap { release -> Release? in
            guard release["draft"] as? Bool != true, release["prerelease"] as? Bool != true,
                  let tag = release["tag_name"] as? String, let version = version(fromTag: tag),
                  let page = (release["html_url"] as? String).flatMap(URL.init(string:)) else { return nil }
            return Release(version: version, page: page)
        }
        .filter { isNewer($0.version, than: current) }
        .max { isNewer($1.version, than: $0.version) }
    }

    static let foundVersionKey = "availableUpdateVersion", foundPageKey = "availableUpdatePage"

    /// The update found earlier, if it's still newer than what's running (once it's installed, it's forgotten).
    static func remembered(running: String, in defaults: UserDefaults) -> Release? {
        guard let version = defaults.string(forKey: foundVersionKey), isNewer(version, than: running),
              let page = defaults.string(forKey: foundPageKey).flatMap(URL.init(string:)) else { return nil }
        return Release(version: version, page: page)
    }

    static func remember(_ release: Release?, in defaults: UserDefaults) {
        defaults.set(release?.version, forKey: foundVersionKey)
        defaults.set(release?.page.absoluteString, forKey: foundPageKey)
    }

    /// Compares dotted versions number by number: 0.10.0 is newer than 0.9.1.
    static func isNewer(_ a: String, than b: String) -> Bool {
        let x = a.split(separator: ".").map { Int($0) ?? 0 }, y = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(x.count, y.count) {
            let p = i < x.count ? x[i] : 0, q = i < y.count ? y[i] : 0
            if p != q { return p > q }
        }
        return false
    }

    /// Asks GitHub for the releases. Nil when offline or GitHub didn't answer: nothing is said about that when
    /// checking quietly.
    static func latest(newerThan current: String) async -> Release?? {
        var request = URLRequest(url: releasesAPI, timeoutInterval: 15)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        guard let (data, response) = try? await URLSession.shared.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200,
              let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return nil }
        return .some(newer(than: current, in: list))
    }
}
