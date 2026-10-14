import CoreLocation
import Foundation
import MapKit

/// Settings > Look Up Place Names (#105): whether the app names the place a photo was taken. Finding the name sends
/// the photo's coordinates to Apple (MapKit's reverse geocoding), so it's off until the person agrees once, as Kelly decided for both
/// apps (12 Oct). Coordinates are shown either way; they come from the photo and never leave the Mac unless this is on.
/// With it on, Get Info names the place, and so do the list's Place Taken column and the gallery's Place taken label for
/// the photos showing (#172).
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
        "To name the place a photo was taken, \(AppInfo.name) sends the photo's coordinates (its latitude and longitude, nothing else) to Apple's location service when you open Get Info, and for the photos showing when Place Taken is a list column or a gallery label. Names found are kept on your Mac, so a place is sent once. Nothing is sent until you turn this on, and you can turn it off at any time in Settings."
    }

    /// Asks Apple for the place's name: nil when the place has none (the open sea), and an error when it couldn't be
    /// asked (offline, or too many lookups), so that isn't remembered. MapKit's MKReverseGeocodingRequest from macOS 26
    /// (#115), CLGeocoder (deprecated in 26) before that. Both ask the same Apple service.
    @Sendable static func geocode(latitude: Double, longitude: Double) async throws -> String? {
        let location = CLLocation(latitude: latitude, longitude: longitude)
        if #available(macOS 26, *) {
            guard let request = MKReverseGeocodingRequest(location: location) else { return nil }
            let items: [MKMapItem]
            do {
                items = try await request.mapItems
            } catch let error as MKError where error.code == .placemarkNotFound {
                return nil
            }
            guard let item = items.first else { return nil }
            // Not MKAddress's fullAddress (a multi-line street address with the postcode) or its shortAddress
            // (street and city, no state or country): neither is the place, city, state, country that Get Info has
            // always read, and Windows reads too. The item's name is the landmark or street address, as the
            // placemark's name was, and cityWithContext(.full) is "Cupertino, CA, United States": city, state
            // and country, with the country always there, as CLGeocoder gave it.
            let address = item.addressRepresentations
            let context: [String?] = address?.cityWithContext(.full)?.components(separatedBy: ", ")
                ?? [address?.cityName, address?.regionName]
            return joined([item.name] + context)
        }
        let placemarks: [CLPlacemark]
        do {
            placemarks = try await CLGeocoder().reverseGeocodeLocation(location)
        } catch let error as CLError where error.code == .geocodeFoundNoResult {
            return nil
        }
        guard let placemark = placemarks.first else { return nil }
        return joined([placemark.name, placemark.locality, placemark.administrativeArea, placemark.country])
    }
}

/// Finds a place's name: nil when it has none, an error when it couldn't be asked. `PlaceNames.geocode`, or a stand-in
/// in tests, which never ask Apple.
typealias PlaceGeocoder = @Sendable (_ latitude: Double, _ longitude: Double) async throws -> String?

/// A spot to about a metre: its latitude and longitude to 5 decimal places, as Windows keys PlaceNames.json, so the same
/// spot is sent once. Kept as whole hundred-thousandths of a degree, so rows can carry one cheaply.
struct PlaceKey: Hashable, Sendable {
    let latitude: Int
    let longitude: Int

    /// Nil for coordinates that aren't on the Earth.
    init?(latitude: Double, longitude: Double) {
        guard latitude.isFinite, longitude.isFinite, abs(latitude) <= 90, abs(longitude) <= 180 else { return nil }
        self.latitude = Int((latitude * 100_000).rounded())
        self.longitude = Int((longitude * 100_000).rounded())
    }

    /// From the key's text in PlaceNames.json ("47.60970,-122.34220").
    init?(_ text: String) {
        let parts = text.split(separator: ",", omittingEmptySubsequences: false)
        guard parts.count == 2,
              let lat = Double(parts[0].trimmingCharacters(in: .whitespaces)),
              let lon = Double(parts[1].trimmingCharacters(in: .whitespaces)) else { return nil }
        self.init(latitude: lat, longitude: lon)
    }

    /// The key as PlaceNames.json writes it, as Windows does: "47.60970,-122.34220".
    var text: String { "\(Self.degrees(latitude)),\(Self.degrees(longitude))" }

    /// The coordinates sent to Apple: the key's own, to about a metre.
    var coordinates: (latitude: Double, longitude: Double) {
        (Double(latitude) / 100_000, Double(longitude) / 100_000)
    }

    private static func degrees(_ value: Int) -> String {
        let magnitude = value.magnitude
        let fraction = String(magnitude % 100_000)
        return "\(value < 0 ? "-" : "")\(magnitude / 100_000).\(String(repeating: "0", count: 5 - fraction.count))\(fraction)"
    }
}

/// Place names already found, kept in PlaceNames.json in the app's data folder, as Windows keeps them, so a spot is
/// sent to Apple once (#172). A spot found to have no name (the open sea) is kept too, as an empty name, so it isn't
/// asked again. Lookups that failed (offline, too many) aren't kept, so they're tried again another time. Read from
/// disk only when first needed.
final class PlaceNameCache: @unchecked Sendable {
    /// Posted (on the main queue) when a place's name is found, so what's showing can say it.
    static let didChange = Notification.Name("PlaceNameCacheDidChange")

    /// The app's, shared by Get Info and the list, in the data folder.
    static let shared = PlaceNameCache()

    static var defaultURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(AppInfo.dataFolderName, isDirectory: true)
            .appendingPathComponent("PlaceNames.json")
    }

    private let fileURL: URL
    private let lock = NSLock()
    private var loaded: [PlaceKey: String]?

    init(fileURL: URL = PlaceNameCache.defaultURL) {
        self.fileURL = fileURL
    }

    /// Every spot known, with its name ("" for none), for filling many rows at once.
    func snapshot() -> [PlaceKey: String] {
        lock.lock()
        defer { lock.unlock() }
        return entries
    }

    /// The name known for a spot: nil when it hasn't been looked up, "" when it has no name.
    func name(for key: PlaceKey) -> String? {
        lock.lock()
        defer { lock.unlock() }
        return entries[key]
    }

    /// Keeps a spot's name (nil or empty for none) and writes the file. One at a time, a second or more apart, so
    /// it's written each time rather than later.
    func remember(_ name: String?, for key: PlaceKey) {
        let name = name?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        lock.lock()
        entries[key] = name
        let snapshot = Dictionary(entries.map { ($0.key.text, $0.value) }, uniquingKeysWith: { first, _ in first })
        lock.unlock()
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        if let data = try? encoder.encode(snapshot) {
            try? FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? data.write(to: fileURL, options: .atomic)
        }
        DispatchQueue.main.async {
            NotificationCenter.default.post(name: Self.didChange, object: self)
        }
    }

    /// Get Info's lookup: the name already known, or asked for now and kept. Nil when there's none or it couldn't
    /// be asked.
    func lookUp(_ key: PlaceKey, using geocode: PlaceGeocoder) async -> String? {
        if let known = name(for: key) { return known.isEmpty ? nil : known }
        let (latitude, longitude) = key.coordinates
        do {
            remember(try await geocode(latitude, longitude), for: key)
            return name(for: key).flatMap { $0.isEmpty ? nil : $0 }
        } catch {
            // Not kept, so it's asked again next time.
            return nil
        }
    }

    /// The entries, read from the file the first time. A file that can't be read is left as it is and starts empty.
    private var entries: [PlaceKey: String] {
        get {
            if let loaded { return loaded }
            var read: [PlaceKey: String] = [:]
            if let data = try? Data(contentsOf: fileURL),
               let saved = try? JSONDecoder().decode([String: String].self, from: data) {
                for (text, name) in saved {
                    if let key = PlaceKey(text) { read[key] = name }
                }
            }
            loaded = read
            return read
        }
        set { loaded = newValue }
    }
}
