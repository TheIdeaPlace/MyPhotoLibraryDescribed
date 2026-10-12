import Foundation
import Photos
import UniformTypeIdentifiers

enum ExportError: LocalizedError {
    case noFile(String)

    var errorDescription: String? {
        switch self {
        case .noFile(let name): "“\(name)” has no file that can be exported."
        }
    }
}

/// Writes library items out as real files, downloading from iCloud when needed.
enum PhotoExporter {
    static let temporaryRoot = FileManager.default.temporaryDirectory
        .appendingPathComponent("PhotoFinder", isDirectory: true)

    private static let originalFilenameSelector = NSSelectorFromString("originalFilename")

    static func originalFilename(for asset: PHAsset) -> String {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *), let name = asset.extendedMetadata.originalFilename, !name.isEmpty {
            return name
        }
#endif
        // Asking PHAssetResource for every asset is a slow round trip to the Photos database.
        // PHAsset already carries the name (undocumented), so use it when present.
        if asset.responds(to: originalFilenameSelector),
           let name = asset.value(forKey: "originalFilename") as? String, !name.isEmpty {
            return name
        }
        let resources = PHAssetResource.assetResources(for: asset)
        let originals: [PHAssetResourceType] = [.photo, .video, .audio]
        let resource = resources.first { originals.contains($0.type) } ?? resources.first
        return resource?.originalFilename ?? "Untitled"
    }

    /// The current version of the item: the edited render if there is one, otherwise the original.
    static func primaryResource(for asset: PHAsset) -> PHAssetResource? {
        let resources = PHAssetResource.assetResources(for: asset)
        let preferred: [PHAssetResourceType] = switch asset.mediaType {
        case .video: [.fullSizeVideo, .video]
        case .audio: [.audio]
        default: [.fullSizePhoto, .photo]
        }
        for type in preferred {
            if let resource = resources.first(where: { $0.type == type }) { return resource }
        }
        return resources.first
    }

    static func uniformType(for asset: PHAsset) -> UTType {
        primaryResource(for: asset).flatMap { UTType($0.uniformTypeIdentifier) } ?? .data
    }

    /// Keeps the original name (IMG_1234) but uses the extension of the file actually exported.
    static func exportFileName(for asset: PHAsset, resource: PHAssetResource) -> String {
        let original = originalFilename(for: asset).replacingOccurrences(of: "/", with: "-")
        let ext = (resource.originalFilename as NSString).pathExtension
        guard !ext.isEmpty else { return original }
        return (original as NSString).deletingPathExtension + "." + ext
    }

    /// Exports into a per-session cache so Quick Look, Open and Copy can reuse the same file.
    static func exportToTemporary(_ asset: PHAsset) async throws -> URL {
        let resource = try requireResource(for: asset)
        let stamp = Int(asset.modificationDate?.timeIntervalSince1970 ?? 0)
        let folder = temporaryRoot.appendingPathComponent("\(safeComponent(asset.localIdentifier))-\(stamp)", isDirectory: true)
        let url = folder.appendingPathComponent(exportFileName(for: asset, resource: resource))
        if FileManager.default.fileExists(atPath: url.path) { return url }
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try await write(resource, to: url, date: asset.creationDate)
        return url
    }

    /// Exports into a folder the user chose, never overwriting an existing file.
    static func export(_ asset: PHAsset, into folder: URL) async throws -> URL {
        let resource = try requireResource(for: asset)
        let url = uniqueURL(in: folder, fileName: exportFileName(for: asset, resource: resource))
        try await write(resource, to: url, date: asset.creationDate)
        return url
    }

    /// Copies a file from an added folder (#36) into a folder the user chose, never overwriting an existing file.
    static func copyFile(_ source: URL, into folder: URL) throws -> URL {
        let url = uniqueURL(in: folder, fileName: source.lastPathComponent)
        try FileManager.default.copyItem(at: source, to: url)
        return url
    }

    private static func requireResource(for asset: PHAsset) throws -> PHAssetResource {
        guard let resource = primaryResource(for: asset) else {
            throw ExportError.noFile(originalFilename(for: asset))
        }
        return resource
    }

    private static func write(_ resource: PHAssetResource, to url: URL, date: Date?) async throws {
        let options = PHAssetResourceRequestOptions()
        options.isNetworkAccessAllowed = true
        let box = UncheckedBox(value: resource)
        do {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                PHAssetResourceManager.default().writeData(for: box.value, toFile: url, options: options) { error in
                    if let error {
                        continuation.resume(throwing: error)
                    } else {
                        continuation.resume()
                    }
                }
            }
        } catch {
            try? FileManager.default.removeItem(at: url)
            throw error
        }
        // Give the file the photo's date so it sorts sensibly in Finder.
        if let date {
            try? FileManager.default.setAttributes([.creationDate: date, .modificationDate: date], ofItemAtPath: url.path)
        }
    }

    private static func uniqueURL(in folder: URL, fileName: String) -> URL {
        var url = folder.appendingPathComponent(fileName)
        let base = (fileName as NSString).deletingPathExtension
        let ext = (fileName as NSString).pathExtension
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            let candidate = ext.isEmpty ? "\(base) \(n)" : "\(base) \(n).\(ext)"
            url = folder.appendingPathComponent(candidate)
            n += 1
        }
        return url
    }

    private static func safeComponent(_ string: String) -> String {
        String(string.map { $0.isLetter || $0.isNumber ? $0 : "_" })
    }
}
