import Darwin
import Foundation
import ImageIO
import UniformTypeIdentifiers

// Writing descriptions into the files of added folders (#31). The Photos library only has a caption, which PhotoKit
// writes; files can also carry IPTC's accessibility fields, which Lightroom, Photoshop and some publishing tools
// read. Everything here is plain ImageIO and file-system work with no PhotoKit, so it's tested on its own.

/// The short and long text IPTC's accessibility fields take from a description (#31).
enum AccessibilityText {
    /// IPTC's guidance for Alt Text (Accessibility): 250 characters or fewer.
    static let altTextLimit = 250

    /// Alt Text (Accessibility): the description when it fits in 250 characters, otherwise the whole sentences from
    /// its start that fit. When even the first sentence is longer, it's cut at a word boundary and ends with "…".
    static func altText(from description: String) -> String {
        let text = description.trimmingCharacters(in: .whitespacesAndNewlines)
        guard text.count > altTextLimit else { return text }
        var fitting = ""
        text.enumerateSubstrings(in: text.startIndex..., options: [.bySentences, .substringNotRequired]) { _, range, _, stop in
            let candidate = text[..<range.upperBound].trimmingCharacters(in: .whitespacesAndNewlines)
            if candidate.count <= altTextLimit { fitting = candidate } else { stop = true }
        }
        if !fitting.isEmpty { return fitting }

        // One long first sentence: as many whole words as fit with the "…".
        let limit = altTextLimit - 1
        let end = text.index(text.startIndex, offsetBy: limit)
        var cut = text[..<end]
        if !text[end].isWhitespace, let space = cut.lastIndex(where: \.isWhitespace) {
            cut = cut[..<space]
        }
        // No comma or dash left hanging before the "…".
        while let last = cut.last, last.isWhitespace || ",;:-–—".contains(last) { cut = cut.dropLast() }
        return cut + "…"
    }

    /// Extended Description (Accessibility): the whole description, only when the alt text had to be shortened.
    static func extendedDescription(from description: String) -> String? {
        let text = description.trimmingCharacters(in: .whitespacesAndNewlines)
        return altText(from: text) == text ? nil : text
    }
}

/// What to change in a file. Nil leaves a field as it is.
struct FileMetadataChanges: Equatable, Sendable {
    /// The caption, XMP's dc:description. Empty removes it.
    var caption: String?
    /// A description for IPTC's accessibility fields: the alt text from it, and the extended description when the alt
    /// text is shorter. Empty removes both.
    var accessibilityDescription: String?
}

enum FileMetadataWriteError: LocalizedError, Equatable {
    /// GIF, WebP, RAW and other formats ImageIO can't change without re-encoding, or at all. The format's name.
    case unsupportedFormat(String)
    case unreadable
    case notWritten(String)

    var errorDescription: String? {
        switch self {
        case .unsupportedFormat(let format): "\(format) files can't hold captions or descriptions."
        case .unreadable: "The file couldn't be read as a photo."
        case .notWritten(let reason): "The file couldn't be changed. \(reason)"
        }
    }
}

enum FileMetadataWriter {
    /// The formats ImageIO changes metadata in without touching the picture. GIF is refused by ImageIO ("not supported
    /// for lossless metadata modification"), WebP can't be written at all, and RAW files belong to the camera maker's
    /// format (#31's research).
    static let writableTypes: Set<String> = [
        UTType.jpeg.identifier, UTType.png.identifier, UTType.tiff.identifier, UTType.heic.identifier,
    ]
    /// The same by file name, for deciding before a file is read.
    static let writableExtensions: Set<String> = ["jpg", "jpeg", "jpe", "jfif", "png", "tif", "tiff", "heic"]

    private static let iptcNamespace = "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/" as CFString
    static let altTextPath = "Iptc4xmpCore:AltTextAccessibility"
    static let extendedDescriptionPath = "Iptc4xmpCore:ExtDescrAccessibility"
    static let captionPath = "dc:description"

    static func canWrite(name: String) -> Bool {
        writableExtensions.contains((name as NSString).pathExtension.lowercased())
    }

    /// What a format is called in messages: "GIF files can't hold captions or descriptions."
    static func formatName(of name: String) -> String {
        if MediaFiles.isRaw(name) { return "RAW" }
        let ext = (name as NSString).pathExtension
        return ext.isEmpty ? "These" : ext.uppercased()
    }

    /// Writes `changes` into the file without re-encoding the picture, keeping every other piece of metadata, and
    /// returns the metadata read back from the file. The new file is written next to the original, read back to check
    /// it, then swapped in, so a failure leaves the original as it was. The file's modified and created dates are put
    /// back afterwards: a file's saved description is keyed by its modified time, and a description changes nothing
    /// a person would call an edit.
    @discardableResult
    static func write(_ changes: FileMetadataChanges, to url: URL) throws -> FileMetadata {
        let name = url.lastPathComponent
        guard canWrite(name: name), !MediaFiles.isRaw(name) else { throw FileMetadataWriteError.unsupportedFormat(formatName(of: name)) }
        guard let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary),
              let type = CGImageSourceGetType(source), CGImageSourceGetCount(source) > 0 else {
            throw FileMetadataWriteError.unreadable
        }
        // Named .jpg but really something else (a GIF renamed): refused like the real thing.
        guard writableTypes.contains(type as String) else {
            let format = UTType(type as String).flatMap(\.preferredFilenameExtension)?.uppercased() ?? formatName(of: name)
            throw FileMetadataWriteError.unsupportedFormat(format)
        }

        // What each field should read once written: nil for removed.
        var expected: [String: String?] = [:]
        if let caption = changes.caption?.trimmingCharacters(in: .whitespacesAndNewlines) {
            expected[captionPath] = caption.isEmpty ? nil : caption
        }
        if let description = changes.accessibilityDescription {
            let alt = AccessibilityText.altText(from: description)
            expected[altTextPath] = alt.isEmpty ? nil : alt
            // A longer description written before leaves its extended description behind unless it's removed.
            expected[extendedDescriptionPath] = alt.isEmpty ? nil : AccessibilityText.extendedDescription(from: description)
        }
        guard !expected.isEmpty else { return FileMetadata.read(url) }

        // Saved dates, to the nanosecond, before anything changes.
        var info = stat()
        guard stat(url.path, &info) == 0 else { throw FileMetadataWriteError.unreadable }
        let created = (try? url.resourceValues(forKeys: [.creationDateKey]))?.creationDate

        // Hidden, so the folder's listing never shows it, and in the same folder, so swapping it in is a rename.
        let temp = url.deletingLastPathComponent()
            .appendingPathComponent(".\(url.deletingPathExtension().lastPathComponent).\(UUID().uuidString).\(url.pathExtension)")
        defer { try? FileManager.default.removeItem(at: temp) }
        // Merged into what's there first, which leaves everything else exactly as it was. A HEIC file whose caption is
        // only in its EXIF (TIFF) description keeps reading the old one that way, so then the file's whole XMP is
        // written out with the change made in it.
        try copy(source, type: type, to: temp, merging: true, expected)
        if !readsBack(temp, expected) {
            try? FileManager.default.removeItem(at: temp)
            try copy(source, type: type, to: temp, merging: false, expected)
            guard readsBack(temp, expected) else { throw FileMetadataWriteError.notWritten("The new metadata didn't read back.") }
        }
        // The same pictures, the same size, before the original is touched: a guard against ImageIO ever dropping one.
        guard samePictures(source, temp) else { throw FileMetadataWriteError.notWritten("The pictures in the new file didn't match.") }

        do {
            _ = try FileManager.default.replaceItemAt(url, withItemAt: temp)
        } catch {
            throw FileMetadataWriteError.notWritten(error.localizedDescription)
        }
        // Creation first: setting it can touch the modified time, which goes back last.
        if let created { try? FileManager.default.setAttributes([.creationDate: created], ofItemAtPath: url.path) }
        var times = [info.st_atimespec, info.st_mtimespec]
        _ = utimensat(AT_FDCWD, url.path, &times, 0)
        return FileMetadata.read(url)
    }

    /// Whether the new file holds as many pictures as the original, the first one the same size.
    private static func samePictures(_ source: CGImageSource, _ temp: URL) -> Bool {
        guard let copy = CGImageSourceCreateWithURL(temp as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary),
              CGImageSourceGetCount(copy) == CGImageSourceGetCount(source) else { return false }
        func size(_ s: CGImageSource) -> (Int, Int)? {
            guard let p = CGImageSourceCopyPropertiesAtIndex(s, 0, nil) as? [CFString: Any] else { return nil }
            return (p[kCGImagePropertyPixelWidth] as? Int ?? -1, p[kCGImagePropertyPixelHeight] as? Int ?? -1)
        }
        guard let a = size(source), let b = size(copy) else { return false }
        return a == b
    }

    /// Copies the picture as it is, its bytes untouched, with the fields in `values` set (or removed, for nil).
    /// Merging adds them to the file's metadata; otherwise the file's XMP is copied, changed and written whole.
    private static func copy(_ source: CGImageSource, type: CFString, to temp: URL, merging: Bool,
                             _ values: [String: String?]) throws {
        let metadata: CGMutableImageMetadata
        if merging {
            metadata = CGImageMetadataCreateMutable()
        } else if let existing = CGImageSourceCopyMetadataAtIndex(source, 0, nil),
                  let copy = CGImageMetadataCreateMutableCopy(existing) {
            metadata = copy
        } else {
            throw FileMetadataWriteError.notWritten("Its metadata couldn't be read.")
        }
        _ = CGImageMetadataRegisterNamespaceForPrefix(metadata, iptcNamespace, "Iptc4xmpCore" as CFString, nil)
        for (path, text) in values {
            // Through the `[x-default]` path: CGImageMetadataTagCreate with an alternate-text type writes an empty
            // rdf:Alt for IPTC's namespace, and makes ImageIO throw on HEIC (#31's research).
            if let text {
                _ = CGImageMetadataSetValueWithPath(metadata, nil, "\(path)[x-default]" as CFString, text as CFString)
            } else if !merging {
                _ = CGImageMetadataRemoveTagWithPath(metadata, nil, path as CFString)
            } else if type as String == UTType.jpeg.identifier {
                // Merging removes a field set to null, but only in JPEG files: PNG, TIFF and HEIC keep it.
                _ = CGImageMetadataSetValueWithPath(metadata, nil, path as CFString, kCFNull)
            } else {
                // There an empty text takes its place, which readers (Photo Finder's too) treat as none, and which
                // also empties the IPTC and TIFF copies of the caption that ImageIO keeps in step with it.
                _ = CGImageMetadataSetValueWithPath(metadata, nil, "\(path)[x-default]" as CFString, "" as CFString)
            }
        }
        guard let destination = CGImageDestinationCreateWithURL(temp as CFURL, type, CGImageSourceGetCount(source), nil) else {
            throw FileMetadataWriteError.notWritten("A new copy couldn't be made in its folder.")
        }
        let options: [CFString: Any] = [kCGImageDestinationMetadata: metadata, kCGImageDestinationMergeMetadata: merging]
        var error: Unmanaged<CFError>?
        guard CGImageDestinationCopyImageSource(destination, source, options as CFDictionary, &error) else {
            let reason = error.map { ($0.takeRetainedValue() as Error).localizedDescription } ?? ""
            throw FileMetadataWriteError.notWritten(reason)
        }
    }

    /// Whether the new copy reads as intended, field by field (an empty field counts as none).
    private static func readsBack(_ url: URL, _ values: [String: String?]) -> Bool {
        guard let written = CGImageSourceCreateWithURL(url as CFURL, nil), CGImageSourceGetCount(written) > 0 else { return false }
        let metadata = CGImageSourceCopyMetadataAtIndex(written, 0, nil)
        return values.allSatisfy { path, value in
            metadata.flatMap { FileMetadata.xmpString($0, path) }.flatMap { $0.isEmpty ? nil : $0 } == value
        }
    }
}

/// What writing into files says once it's done (#31): "Saved to 3 files. Skipped anim.gif: GIF files can't hold
/// captions or descriptions. 1 photo hasn't been described yet".
enum FileWriteSummary {
    /// The files whose format can't hold a caption or description, by name and format.
    static func unsupported(_ files: [MediaFile]) -> [(name: String, format: String)] {
        files.filter { !FileMetadataWriter.canWrite(name: $0.name) }.map { ($0.name, FileMetadataWriter.formatName(of: $0.name)) }
    }

    /// Names the one file skipped, or counts them, with the formats that can't be written.
    static func skippedText(_ skipped: [(name: String, format: String)]) -> String? {
        guard let first = skipped.first else { return nil }
        var formats: [String] = []
        for item in skipped where !formats.contains(item.format) { formats.append(item.format) }
        let list = formats.count == 1 ? formats[0] : formats.dropLast().joined(separator: ", ") + " and " + formats[formats.count - 1]
        let what = skipped.count == 1 ? "Skipped \(first.name)" : "Skipped \(skipped.count) files"
        return "\(what): \(list) files can't hold captions or descriptions"
    }

    /// `done` (nil when nothing was written), then what was skipped and what wasn't described.
    static func text(done: String?, skipped: [(name: String, format: String)], notDescribed: Int) -> String {
        var parts: [String] = []
        if let done { parts.append(done) }
        if let text = skippedText(skipped) { parts.append(text) }
        if notDescribed > 0 {
            parts.append(notDescribed == 1 ? "1 photo hasn't been described yet" : "\(notDescribed) photos haven't been described yet")
        }
        return parts.joined(separator: ". ")
    }
}

/// What a description would replace in a file, and whether automatic captions may write its alt text (#31).
enum FileFields {
    /// The fields holding something else now: a caption (when `caption` is given) and alt text, each with what it
    /// holds. Empty fields and fields already holding what would be written aren't counted.
    static func replaced(caption: String?, altText: String, with description: String) -> [(field: String, current: String)] {
        var fields: [(field: String, current: String)] = []
        if let caption, !caption.isEmpty, caption != description { fields.append(("caption", caption)) }
        if !altText.isEmpty, altText != AccessibilityText.altText(from: description) { fields.append(("alt text", altText)) }
        return fields
    }

    /// Save descriptions as captions writes alt text that's empty or already this description's; alt text another app
    /// wrote only under Always.
    static func mayWriteAltText(_ description: String, over existing: String, policy: CaptionPolicy) -> Bool {
        switch policy {
        case .never: false
        case .always: true
        case .whenEmpty: existing.isEmpty || existing == AccessibilityText.altText(from: description)
        }
    }
}
