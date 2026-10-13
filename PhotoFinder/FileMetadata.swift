import AppKit
import AVFoundation
import ImageIO
import QuickLookThumbnailing

/// What a photo or video file in an added folder says about itself (#36), read with ImageIO (or AVFoundation for
/// videos) without decoding the picture.
struct FileMetadata: Hashable, Sendable {
    var width = 0
    var height = 0
    var dateTaken: Date?
    var caption = ""
    /// IPTC's Alt Text (Accessibility) and Extended Description (Accessibility), which My Photo Library Described writes (#31).
    var altText = ""
    var extendedDescription = ""
    var keywords: [String] = []
    var rating = 0
    var latitude: Double?
    var longitude: Double?
    var duration: Double = 0

    /// Reads a photo's metadata. Nothing is decoded, so this is quick even for large files.
    static func read(_ url: URL) -> FileMetadata {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] else {
            return FileMetadata()
        }
        return from(properties: properties, xmp: CGImageSourceCopyMetadataAtIndex(source, 0, nil))
    }

    /// Reads a video's length and picture size.
    static func readVideo(_ url: URL) async -> FileMetadata {
        let asset = AVURLAsset(url: url)
        var metadata = FileMetadata()
        if let duration = try? await asset.load(.duration), duration.isNumeric {
            metadata.duration = duration.seconds
        }
        if let track = try? await asset.loadTracks(withMediaType: .video).first,
           let (size, transform) = try? await track.load(.naturalSize, .preferredTransform) {
            let shown = size.applying(transform)
            metadata.width = Int(abs(shown.width).rounded())
            metadata.height = Int(abs(shown.height).rounded())
        }
        if let date = try? await asset.load(.creationDate), let value = try? await date.load(.dateValue) {
            metadata.dateTaken = value
        }
        return metadata
    }

    /// The fields from ImageIO's properties and XMP. The caption is the first of IPTC's Caption-Abstract, XMP's
    /// dc:description, the TIFF ImageDescription and XMP's dc:title: Photos and most Mac apps write the first three,
    /// and Windows' File Explorer (and so the Windows app) writes the title.
    static func from(properties: [CFString: Any], xmp: CGImageMetadata?) -> FileMetadata {
        let tiff = properties[kCGImagePropertyTIFFDictionary] as? [CFString: Any] ?? [:]
        let exif = properties[kCGImagePropertyExifDictionary] as? [CFString: Any] ?? [:]
        let iptc = properties[kCGImagePropertyIPTCDictionary] as? [CFString: Any] ?? [:]
        let gps = properties[kCGImagePropertyGPSDictionary] as? [CFString: Any] ?? [:]
        var metadata = FileMetadata()

        let width = properties[kCGImagePropertyPixelWidth] as? Int ?? 0
        let height = properties[kCGImagePropertyPixelHeight] as? Int ?? 0
        // Orientations 5 to 8 are turned a quarter, so the photo is shown taller than it's stored, or wider.
        let orientation = properties[kCGImagePropertyOrientation] as? Int ?? 1
        (metadata.width, metadata.height) = (5...8).contains(orientation) ? (height, width) : (width, height)

        if let original = exif[kCGImagePropertyExifDateTimeOriginal] as? String {
            metadata.dateTaken = exifDate(original, offset: exif[kCGImagePropertyExifOffsetTimeOriginal] as? String)
        } else if let digitized = exif[kCGImagePropertyExifDateTimeDigitized] as? String {
            metadata.dateTaken = exifDate(digitized, offset: exif[kCGImagePropertyExifOffsetTimeDigitized] as? String)
        }

        let captions: [String?] = [
            iptc[kCGImagePropertyIPTCCaptionAbstract] as? String,
            xmp.flatMap { xmpString($0, "dc:description") },
            tiff[kCGImagePropertyTIFFImageDescription] as? String,
            xmp.flatMap { xmpString($0, "dc:title") },
        ]
        metadata.caption = captions.lazy.compactMap { $0?.trimmingCharacters(in: .whitespacesAndNewlines) }
            .first { !$0.isEmpty } ?? ""

        let trimmed = { (path: String) in
            xmp.flatMap { xmpString($0, path) }?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        }
        metadata.altText = trimmed(FileMetadataWriter.altTextPath)
        metadata.extendedDescription = trimmed(FileMetadataWriter.extendedDescriptionPath)

        var keywords = (iptc[kCGImagePropertyIPTCKeywords] as? [String]) ?? []
        if keywords.isEmpty, let xmp { keywords = xmpStrings(xmp, "dc:subject") }
        metadata.keywords = keywords.map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }

        if let xmp, let text = xmpString(xmp, "xmp:Rating"), let stars = Int(text) {
            metadata.rating = min(max(stars, 0), 5)
        } else if let stars = iptc[kCGImagePropertyIPTCStarRating] as? Int {
            metadata.rating = min(max(stars, 0), 5)
        }

        if let lat = gps[kCGImagePropertyGPSLatitude] as? Double, let lon = gps[kCGImagePropertyGPSLongitude] as? Double {
            metadata.latitude = (gps[kCGImagePropertyGPSLatitudeRef] as? String) == "S" ? -lat : lat
            metadata.longitude = (gps[kCGImagePropertyGPSLongitudeRef] as? String) == "W" ? -lon : lon
        }
        return metadata
    }

    /// EXIF writes dates as "2024:06:01 14:30:00" in the camera's local time, sometimes with an offset ("+02:00").
    /// Without one, the time is taken as this Mac's time zone, as Photos does.
    static func exifDate(_ text: String, offset: String? = nil) -> Date? {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = .current
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        if let offset, !offset.isEmpty {
            formatter.dateFormat = "yyyy:MM:dd HH:mm:ssxxx"
            if let date = formatter.date(from: trimmed + offset) { return date }
        }
        formatter.dateFormat = "yyyy:MM:dd HH:mm:ss"
        return formatter.date(from: trimmed)
    }

    static func xmpString(_ metadata: CGImageMetadata, _ path: String) -> String? {
        guard let tag = CGImageMetadataCopyTagWithPath(metadata, nil, path as CFString) else { return nil }
        let value = CGImageMetadataTagCopyValue(tag)
        if let text = value as? String { return text }
        // Language alternatives (dc:description, dc:title) are an array of the same text in each language.
        if let items = value as? [CGImageMetadataTag], let first = items.first {
            return CGImageMetadataTagCopyValue(first) as? String
        }
        return nil
    }

    private static func xmpStrings(_ metadata: CGImageMetadata, _ path: String) -> [String] {
        guard let tag = CGImageMetadataCopyTagWithPath(metadata, nil, path as CFString),
              let items = CGImageMetadataTagCopyValue(tag) as? [CGImageMetadataTag] else { return [] }
        return items.compactMap { CGImageMetadataTagCopyValue($0) as? String }
    }
}

/// Pictures of files in added folders, for the list's thumbnails and the gallery.
enum FileImages {
    /// A photo shrunk to fit `maxPixels`, already turned upright. ImageIO decodes only what's needed.
    static func image(_ url: URL, maxPixels: Int) async -> NSImage? {
        if MediaFiles.isVideo(url.lastPathComponent) { return await thumbnail(url, size: CGFloat(maxPixels)) }
        return await Task.detached(priority: .userInitiated) { () -> NSImage? in
            guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
            let options: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: maxPixels,
            ]
            guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return nil }
            return NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
        }.value
    }

    /// Quick Look's thumbnail, which also covers videos and RAW files.
    static func thumbnail(_ url: URL, size: CGFloat) async -> NSImage? {
        let request = QLThumbnailGenerator.Request(fileAt: url, size: CGSize(width: size, height: size),
                                                   scale: 2,
                                                   representationTypes: .thumbnail)
        return try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: request).nsImage
    }
}
