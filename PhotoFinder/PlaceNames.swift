import Foundation

/// Settings > Look Up Place Names (#105): whether Get Info names the place a photo was taken. Finding the name sends
/// the photo's coordinates to Apple (MapKit's reverse geocoding), so it's off until the person agrees once, as Kelly decided for both
/// apps (12 Oct). Coordinates are shown either way; they come from the photo and never leave the Mac unless this is on.
enum PlaceNames {
    static let key = "lookUpPlaceNames"

    static func isOn(in defaults: UserDefaults) -> Bool { defaults.bool(forKey: key) }

    /// A place's name from its parts, most particular first ("Golden Gate Park, San Francisco, CA, United States"),
    /// leaving out empty parts and repeats, as when a city is also the place's name.
    static func joined(_ parts: [String?]) -> String? {
        var kept: [String] = []
        for part in parts {
            if let part = part?.trimmingCharacters(in: .whitespaces), !part.isEmpty, !kept.contains(part) { kept.append(part) }
        }
        return kept.isEmpty ? nil : kept.joined(separator: ", ")
    }

    static let question = "Look up place names?"
    static var explanation: String {
        "To name the place a photo was taken, \(AppInfo.name) sends the photo's coordinates (its latitude and longitude, nothing else) to Apple's location service when you open Get Info. Nothing is sent until you turn this on, and you can turn it off at any time in Settings."
    }
}
