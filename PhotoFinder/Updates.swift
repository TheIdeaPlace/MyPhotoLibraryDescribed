import AppKit
import Foundation

/// Help > Check for Updates (#22, #33): the Mac version's releases are GitHub releases tagged `mac-v<version>`, with a
/// signed, notarized disk image. The app looks for a newer one quietly once a day and, if there is one, the menu item
/// says so ("Update to 0.2.0…"), without taking focus or interrupting, as the app kit asks. Choosing it opens the
/// release to download. No updater framework: a person drags the new app into Applications, as with most Mac apps.
enum Updates {
    static let tagPrefix = "mac-v"
    static let releasesAPI = URL(string: "https://api.github.com/repos/kellylford/PhotoFinder/releases?per_page=30")!

    struct Release: Equatable {
        let version: String
        let page: URL
    }

    /// The newest Mac release that's newer than `current`, from GitHub's list of releases (drafts and pre-releases
    /// left out), or nil.
    static func newer(than current: String, in releases: [[String: Any]]) -> Release? {
        releases.compactMap { release -> Release? in
            guard release["draft"] as? Bool != true, release["prerelease"] as? Bool != true,
                  let tag = release["tag_name"] as? String, tag.hasPrefix(tagPrefix),
                  let page = (release["html_url"] as? String).flatMap(URL.init(string:)) else { return nil }
            return Release(version: String(tag.dropFirst(tagPrefix.count)), page: page)
        }
        .filter { isNewer($0.version, than: current) }
        .max { isNewer($1.version, than: $0.version) }
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
