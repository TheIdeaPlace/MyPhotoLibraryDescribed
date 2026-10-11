import Foundation
import os
import Photos
import UniformTypeIdentifiers

enum SidebarID {
    static let allPhotos = "library:all"
}

/// Where the rows in the file list come from.
enum SourceKind: Hashable {
    case allPhotos
    case assetCollection(String)
    case collectionList(String)
    case year(Int)
    case month(year: Int, month: Int)
}

/// A place in the sidebar. Items with `children` behave like folders: selecting one lists its children.
struct SidebarItem: Identifiable, Hashable {
    let id: String
    var title: String
    var kind: SourceKind
    var systemImage: String
    var count: Int
    var children: [SidebarItem]?
    var parentID: String?
    /// Shown in the Kind column when this item appears as a row, e.g. "Album" or "Folder".
    var containerKind: String
}

struct SidebarSection: Identifiable, Hashable {
    let id: String
    let title: String
    var items: [SidebarItem]
}

/// One row in the Finder-style list: either a photo/video or a container (album, folder, year, month).
struct FileRow: Identifiable, Hashable, Sendable {
    enum Payload: Hashable, Sendable { case container, photo }

    let id: String
    let payload: Payload
    private(set) var name: String
    private(set) var fileType: String
    let symbol: String
    let date: Date?
    let dateText: String
    let modified: Date?
    let modifiedText: String
    let kind: String
    let pixelCount: Int
    let dimensions: String
    let orientation: String
    let megapixels: Double
    let megapixelsText: String
    let duration: Double
    let durationText: String
    let isFavorite: Bool
    private(set) var caption: String
    private(set) var keywords: String
    let rating: Int
    let ratingText: String
    let locationText: String
    let source: String

    var isContainer: Bool { payload == .container }
    var sortDate: Date { date ?? .distantPast }
    var sortModified: Date { modified ?? .distantPast }
    var favoriteSort: Int { isFavorite ? 1 : 0 }

    /// What VoiceOver hears for this item outside the list: name, kind, date and caption.
    var spokenDescription: String {
        var parts = [name, kind]
        if !dateText.isEmpty { parts.append(dateText) }
        if !caption.isEmpty { parts.append("Caption: \(caption)") }
        return parts.joined(separator: ", ")
    }

    private static let dateStyle = Date.FormatStyle(date: .abbreviated, time: .shortened)

    /// `name` is nil when the original file name hasn't been looked up yet; the kind and date stand in until it is.
    init(asset: PHAsset, name: String?) {
        id = asset.localIdentifier
        payload = .photo
        symbol = asset.mediaType == .video ? "video" : "photo"
        date = asset.creationDate
        dateText = asset.creationDate.map { Self.dateStyle.format($0) } ?? ""
        modified = asset.modificationDate
        modifiedText = asset.modificationDate.map { Self.dateStyle.format($0) } ?? ""
        kind = Self.kindDescription(for: asset)
        let width = asset.pixelWidth, height = asset.pixelHeight
        pixelCount = width * height
        dimensions = width > 0 ? "\(width) × \(height)" : ""
        orientation = width == 0 ? "" : width > height ? "Landscape" : width < height ? "Portrait" : "Square"
        megapixels = Double(pixelCount) / 1_000_000
        megapixelsText = pixelCount > 0 ? "\(megapixels.formatted(.number.precision(.fractionLength(1)))) MP" : ""
        duration = asset.mediaType == .video ? asset.duration : 0
        durationText = asset.mediaType == .video ? Self.durationText(asset.duration) : ""
        isFavorite = asset.isFavorite
        if let c = asset.location?.coordinate {
            locationText = String(format: "%.4f, %.4f", c.latitude, c.longitude)
        } else {
            locationText = ""
        }
        source = switch asset.sourceType {
        case .typeCloudShared: "Shared Album"
        case .typeiTunesSynced: "Synced"
        default: "My Library"
        }

        // Captions and keywords are a database round trip per item, so they arrive later via withDetails.
        caption = ""
        keywords = ""
        var rating = 0
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            rating = asset.rating.rawValue
        }
#endif
        self.rating = rating
        ratingText = rating == 0 ? "" : rating == 1 ? "1 star" : "\(rating) stars"

        self.name = name ?? (dateText.isEmpty ? kind : "\(kind), \(dateText)")
        fileType = name.map(Self.fileTypeDescription) ?? ""
    }

    func withDetails(_ details: AssetDetails) -> FileRow {
        var copy = self
        copy.name = details.name
        copy.fileType = Self.fileTypeDescription(for: details.name)
        copy.caption = details.caption
        copy.keywords = details.keywords
        return copy
    }

    init(container item: SidebarItem) {
        id = item.id
        payload = .container
        name = item.title
        symbol = item.systemImage
        switch item.kind {
        case .month(let year, let month):
            date = Calendar.current.date(from: DateComponents(year: year, month: month, day: 1))
        case .year(let year):
            date = Calendar.current.date(from: DateComponents(year: year, month: 1, day: 1))
        default:
            date = nil
        }
        dateText = ""
        let countText = item.count == 1 ? "1 item" : "\(item.count.formatted()) items"
        kind = "\(item.containerKind), \(countText)"
        fileType = item.containerKind
        modified = nil
        modifiedText = ""
        pixelCount = 0
        dimensions = ""
        orientation = ""
        megapixels = 0
        megapixelsText = ""
        duration = 0
        durationText = ""
        isFavorite = false
        caption = ""
        keywords = ""
        rating = 0
        ratingText = ""
        locationText = ""
        source = ""
    }

    private static let fileTypeCache = OSAllocatedUnfairLock(initialState: [String: String]())

    /// "HEIF Image", "QuickTime movie" and so on. Looking these up is slow, so they're cached by extension.
    private static func fileTypeDescription(for fileName: String) -> String {
        let ext = (fileName as NSString).pathExtension.lowercased()
        guard !ext.isEmpty else { return "" }
        if let cached = fileTypeCache.withLock({ $0[ext] }) { return cached }
        let description = UTType(filenameExtension: ext)?.localizedDescription ?? ext.uppercased()
        fileTypeCache.withLock { $0[ext] = description }
        return description
    }

    static func kindDescription(for asset: PHAsset) -> String {
        let subtypes = asset.mediaSubtypes
        switch asset.mediaType {
        case .image:
            if subtypes.contains(.photoScreenshot) { return "Screenshot" }
            if subtypes.contains(.photoLive) { return "Live Photo" }
            if subtypes.contains(.photoPanorama) { return "Panorama" }
            if subtypes.contains(.photoDepthEffect) { return "Portrait Photo" }
            if asset.representsBurst { return "Burst Photo" }
            if subtypes.contains(.photoHDR) { return "HDR Photo" }
            return "Photo"
        case .video:
            if subtypes.contains(.videoHighFrameRate) { return "Slo-mo Video" }
            if subtypes.contains(.videoTimelapse) { return "Time-lapse Video" }
            if subtypes.contains(.videoCinematic) { return "Cinematic Video" }
            return "Video"
        case .audio:
            return "Audio"
        default:
            return "Unknown"
        }
    }

    static func durationText(_ seconds: Double) -> String {
        let pattern: Duration.TimeFormatStyle.Pattern = seconds >= 3600 ? .hourMinuteSecond : .minuteSecond
        return Duration.seconds(seconds.rounded()).formatted(.time(pattern: pattern))
    }
}

/// Lets non-Sendable Photos objects cross into background tasks. PhotoKit objects are immutable snapshots.
struct UncheckedBox<Value>: @unchecked Sendable {
    let value: Value
}

/// Original file names take a Photos database round trip per item, so they are looked up
/// in the background and saved to disk; after the first pass every album opens instantly.
final class NameCache: @unchecked Sendable {
    private var names: [String: String] = [:]
    private var unsaved = 0
    private let lock = NSLock()
    private let fileURL: URL

    init() {
        let folder = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("PhotoFinder", isDirectory: true)
        fileURL = folder.appendingPathComponent("FileNames.plist")
        if let data = try? Data(contentsOf: fileURL),
           let saved = try? PropertyListDecoder().decode([String: String].self, from: data) {
            names = saved
        }
    }

    func cachedName(for id: String) -> String? {
        lock.lock()
        defer { lock.unlock() }
        return names[id]
    }

    func remember(_ name: String, for id: String) {
        lock.lock()
        if names[id] != name {
            names[id] = name
            unsaved += 1
        }
        lock.unlock()
    }

    func name(for asset: PHAsset) -> String {
        if let cached = cachedName(for: asset.localIdentifier) { return cached }
        let name = PhotoExporter.originalFilename(for: asset)
        lock.lock()
        names[asset.localIdentifier] = name
        unsaved += 1
        lock.unlock()
        return name
    }

    func save() {
        lock.lock()
        guard unsaved > 0 else { lock.unlock(); return }
        let snapshot = names
        unsaved = 0
        lock.unlock()
        let encoder = PropertyListEncoder()
        encoder.outputFormat = .binary
        guard let data = try? encoder.encode(snapshot) else { return }
        try? FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
    }
}

/// The slow-to-read parts of an item: original file name, caption and keywords.
struct AssetDetails: Sendable {
    let name: String
    let caption: String
    let keywords: String

    static func load(for asset: PHAsset, names: NameCache) -> AssetDetails {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            let metadata = asset.extendedMetadata
            let name = metadata.originalFilename.flatMap { $0.isEmpty ? nil : $0 } ?? names.name(for: asset)
            names.remember(name, for: asset.localIdentifier)
            return AssetDetails(name: name, caption: metadata.caption ?? "",
                                keywords: metadata.keywords.joined(separator: ", "))
        }
#endif
        return AssetDetails(name: names.name(for: asset), caption: "", keywords: "")
    }
}

enum RowBuilder {
    /// Fetch options that load file names and captions in bulk on macOS 27 and later.
    static func fetchOptions() -> PHFetchOptions {
        let options = PHFetchOptions()
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            options.prefetchAssetExtendedMetadata = true
        }
#endif
        return options
    }

    /// Builds rows from cached names only, so it never waits on the Photos database.
    /// Returns the assets whose details still need reading.
    static func build(_ fetch: PHFetchResult<PHAsset>, names: NameCache)
        -> UncheckedBox<(rows: [FileRow], assets: [String: PHAsset], needingDetails: [PHAsset])> {
        let assets = fetch.objects(at: IndexSet(integersIn: 0..<fetch.count))
        var rows: [FileRow] = []
        rows.reserveCapacity(assets.count)
        var byID: [String: PHAsset] = Dictionary(minimumCapacity: assets.count)
        var needingDetails: [PHAsset] = []
        let hasExtendedMetadata: Bool
        if #available(macOS 27, *) { hasExtendedMetadata = true } else { hasExtendedMetadata = false }
        for asset in assets {
            byID[asset.localIdentifier] = asset
            let name = names.cachedName(for: asset.localIdentifier)
            // On macOS 27 every item has a caption and keywords to read; before that, only missing names.
            if hasExtendedMetadata || name == nil { needingDetails.append(asset) }
            rows.append(FileRow(asset: asset, name: name))
        }
        return UncheckedBox(value: (rows, byID, needingDetails))
    }
}
