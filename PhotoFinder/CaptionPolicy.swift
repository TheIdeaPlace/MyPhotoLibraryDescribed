import Foundation

/// Photo > Save Descriptions in Photos (#30, #31): whether each new description is also saved in the photo itself, as
/// its caption when it has none (in Photos, or in the file), and in a file's accessibility fields when they're empty.
/// One on/off rather than a choice of rules, since the app should decide for people (Kelly, 12 Oct); it's off until
/// the person agrees once, because these are their own photos. Nothing a person or another app wrote is replaced.
enum CaptionPolicy: String {
    case never, whenEmpty

    static let key = "saveDescriptionsInPhotos"
    /// The three-way setting before it became one switch: "never", "whenEmpty" or "always".
    static let oldKey = "saveDescriptionsAsCaptions"

    static var current: CaptionPolicy { current(in: .standard) }

    static func current(in defaults: UserDefaults) -> CaptionPolicy {
        isOn(in: defaults) ? .whenEmpty : .never
    }

    /// On if the person turned it on, or chose either of the old settings that wrote captions ("Always" becomes the
    /// safer "only when empty").
    static func isOn(in defaults: UserDefaults) -> Bool {
        if let on = defaults.object(forKey: key) as? Bool { return on }
        return ["whenEmpty", "always"].contains(defaults.string(forKey: oldKey) ?? "")
    }

    /// Whether a new description goes into a photo whose caption is `existing`: only when it has none.
    func shouldWrite(_ description: String, over existing: String) -> Bool {
        let existing = existing.trimmingCharacters(in: .whitespacesAndNewlines)
        let description = description.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !description.isEmpty, existing != description else { return false }
        return self == .whenEmpty && existing.isEmpty
    }
}
