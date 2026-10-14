import Foundation

/// What Apple Intelligence is asked when it describes a photo: Describe Photo, Get Info's Describe Image and
/// background describing. Settings can change it; most people never will, so an empty setting means the standard one.
enum DescriptionPrompt {
    static let key = "describePrompt"

    static let standard = """
        Describe this photo. Start with a one-sentence summary, then give the important details: \
        people and what they are doing, the setting, notable objects, colors and lighting, \
        and any text that appears in the photo.
        """

    static var current: String { current(in: .standard) }

    /// The prompt in use with these settings: the app's own, or a test's.
    static func current(in defaults: UserDefaults) -> String { resolve(defaults.string(forKey: key)) }

    /// The saved prompt, or the standard one when nothing (or only spaces) was saved.
    static func resolve(_ saved: String?) -> String {
        let trimmed = saved?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return trimmed.isEmpty ? standard : trimmed
    }
}
