import Foundation

/// Settings > Save descriptions as captions (#30): whether a new description is also written into the photo's
/// caption, where Photos, iCloud Photos and other apps see it. Off unless chosen: a caption is the person's own words.
enum CaptionPolicy: String, CaseIterable, Identifiable {
    case never, whenEmpty, always

    static let key = "saveDescriptionsAsCaptions"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .never: "Never"
        case .whenEmpty: "Only When There's No Caption"
        case .always: "Always"
        }
    }

    static var current: CaptionPolicy {
        UserDefaults.standard.string(forKey: key).flatMap(CaptionPolicy.init) ?? .never
    }

    /// Whether a new description goes into a photo whose caption is `existing`.
    func shouldWrite(_ description: String, over existing: String) -> Bool {
        let existing = existing.trimmingCharacters(in: .whitespacesAndNewlines)
        let description = description.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !description.isEmpty, existing != description else { return false }
        switch self {
        case .never: return false
        case .whenEmpty: return existing.isEmpty
        case .always: return true
        }
    }
}
