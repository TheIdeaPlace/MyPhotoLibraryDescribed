import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import Photo_Finder

/// Writing descriptions into files (#31): real JPEG, PNG, TIFF and HEIC files made in a temporary folder, changed
/// and read back.
final class FileMetadataWriterTests: XCTestCase {
    private var temp: URL!

    override func setUpWithError() throws {
        // These tests write into files, so changes are allowed while they run (#84); the app starts read-only.
        PhotoChanges.allowed = true
        temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        PhotoChanges.allowed = false
        FileMetadataWriter.beforeReplacing = nil
        try? FileManager.default.removeItem(at: temp)
    }

    private static let formats: [(String, UTType)] = [("jpg", .jpeg), ("png", .png), ("tiff", .tiff), ("heic", .heic)]

    /// A small photo with a camera date, a title and keywords, like one a camera or another app wrote.
    private func makePhoto(_ name: String, type: UTType, caption: String? = nil, title: String? = "Original title") throws -> URL {
        let url = temp.appendingPathComponent(name)
        let width = 64, height = 48
        let context = try XCTUnwrap(CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                              space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        for y in stride(from: 0, to: height, by: 8) {
            for x in stride(from: 0, to: width, by: 8) {
                context.setFillColor(red: CGFloat(x) / CGFloat(width), green: CGFloat(y) / CGFloat(height), blue: 0.5, alpha: 1)
                context.fill(CGRect(x: x, y: y, width: 8, height: 8))
            }
        }
        let image = try XCTUnwrap(context.makeImage())
        let xmp = CGImageMetadataCreateMutable()
        if let title { _ = CGImageMetadataSetValueWithPath(xmp, nil, "dc:title[x-default]" as CFString, title as CFString) }
        let keywords = try XCTUnwrap(CGImageMetadataTagCreate("http://purl.org/dc/elements/1.1/" as CFString, "dc" as CFString,
                                                              "subject" as CFString, .arrayUnordered, ["beach", "dog"] as CFArray))
        _ = CGImageMetadataSetTagWithPath(xmp, nil, "dc:subject" as CFString, keywords)
        if let caption {
            _ = CGImageMetadataSetValueWithPath(xmp, nil, "dc:description[x-default]" as CFString, caption as CFString)
        }
        let properties: [CFString: Any] = [
            kCGImagePropertyExifDictionary: [kCGImagePropertyExifDateTimeOriginal: "2024:06:01 10:11:12"],
            kCGImagePropertyGPSDictionary: [kCGImagePropertyGPSLatitude: 47.6, kCGImagePropertyGPSLatitudeRef: "N",
                                            kCGImagePropertyGPSLongitude: 122.3, kCGImagePropertyGPSLongitudeRef: "W"],
            kCGImageDestinationLossyCompressionQuality: 0.8,
        ]
        let destination = try XCTUnwrap(CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, 1, nil))
        CGImageDestinationAddImageAndMetadata(destination, image, xmp, properties as CFDictionary)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        // An old modified date, as photos usually have, so keeping it is a real test.
        let old = Date(timeIntervalSince1970: 1_700_000_000.123456)
        try FileManager.default.setAttributes([.modificationDate: old, .creationDate: old.addingTimeInterval(-60)],
                                              ofItemAtPath: url.path)
        return url
    }

    private func modified(_ url: URL) -> timespec {
        var info = stat()
        stat(url.path, &info)
        return info.st_mtimespec
    }

    private func pixels(_ url: URL) -> Data? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return nil }
        return image.dataProvider?.data as Data?
    }

    /// A JPEG's compressed picture, from the start-of-scan marker on.
    private func jpegScan(_ url: URL) throws -> Data {
        let data = try Data(contentsOf: url)
        let start = try XCTUnwrap(data.range(of: Data([0xFF, 0xDA])))
        return data.subdata(in: start.lowerBound..<data.count)
    }

    private let short = "A brown dog runs on a sandy beach."

    // MARK: - Writing

    func testEachFormatGetsCaptionAndAltTextAndKeepsEverythingElse() throws {
        for (ext, type) in Self.formats {
            let url = try makePhoto("photo.\(ext)", type: type)
            let before = FileMetadata.read(url)
            let pixelsBefore = pixels(url)
            let date = modified(url)

            let after = try FileMetadataWriter.write(.init(caption: short, accessibilityDescription: short), to: url)

            XCTAssertEqual(after.caption, short, ext)
            XCTAssertEqual(after.altText, short, ext)
            XCTAssertEqual(after.extendedDescription, "", ext)
            XCTAssertEqual(after.keywords, ["beach", "dog"], ext)
            XCTAssertEqual(after.dateTaken, before.dateTaken, ext)
            XCTAssertNotNil(after.dateTaken, ext)
            XCTAssertEqual(after.latitude, before.latitude, ext)
            XCTAssertEqual(after.width, 64, ext)
            let xmp = try XCTUnwrap(CGImageSourceCopyMetadataAtIndex(CGImageSourceCreateWithURL(url as CFURL, nil)!, 0, nil))
            XCTAssertEqual(FileMetadata.xmpString(xmp, "dc:title"), "Original title", ext)
            XCTAssertEqual(pixels(url), pixelsBefore, "\(ext): the picture changed")
            let now = modified(url)
            XCTAssertEqual(now.tv_sec, date.tv_sec, ext)
            XCTAssertEqual(now.tv_nsec, date.tv_nsec, ext)
        }
    }

    func testAJPEGsPictureIsNotReencoded() throws {
        let url = try makePhoto("photo.jpg", type: .jpeg)
        let scan = try jpegScan(url)
        try FileMetadataWriter.write(.init(caption: short, accessibilityDescription: short), to: url)
        XCTAssertEqual(try jpegScan(url), scan)
    }

    func testTheFileKeepsItsDatesSoItsDescriptionKeyKeepsItsTime() throws {
        let url = try makePhoto("photo.jpg", type: .jpeg)
        let values = try url.resourceValues(forKeys: [.contentModificationDateKey, .creationDateKey])
        try FileMetadataWriter.write(.init(caption: short), to: url)
        let after = try URL(fileURLWithPath: url.path).resourceValues(forKeys: [.contentModificationDateKey, .creationDateKey])
        XCTAssertEqual(after.contentModificationDate.map(FileDescriptionKey.ticks), values.contentModificationDate.map(FileDescriptionKey.ticks))
        XCTAssertEqual(after.creationDate, values.creationDate)
    }

    func testNoTemporaryFileIsLeftBehind() throws {
        let url = try makePhoto("photo.jpg", type: .jpeg)
        try FileMetadataWriter.write(.init(accessibilityDescription: short), to: url)
        let names = try FileManager.default.contentsOfDirectory(atPath: temp.path)
        XCTAssertEqual(names, ["photo.jpg"])
    }

    func testALongDescriptionGoesInBothFieldsAndAShortOneRemovesTheExtendedOne() throws {
        for (ext, type) in Self.formats {
            let url = try makePhoto("photo.\(ext)", type: type)
            let long = String(repeating: "A brown dog runs along the beach at low tide. ", count: 8).trimmingCharacters(in: .whitespaces)
            var read = try FileMetadataWriter.write(.init(accessibilityDescription: long), to: url)
            XCTAssertEqual(read.altText, AccessibilityText.altText(from: long), ext)
            XCTAssertLessThanOrEqual(read.altText.count, 250, ext)
            XCTAssertEqual(read.extendedDescription, long, ext)
            XCTAssertEqual(read.caption, "Original title", "\(ext): the caption is left alone")

            read = try FileMetadataWriter.write(.init(accessibilityDescription: short), to: url)
            XCTAssertEqual(read.altText, short, ext)
            XCTAssertEqual(read.extendedDescription, "", "\(ext): a stale extended description is removed")
        }
    }

    func testASecondCaptionReplacesTheFirstAndAnEmptyOneRemovesIt() throws {
        for (ext, type) in Self.formats {
            let url = try makePhoto("photo.\(ext)", type: type, caption: "Rex at Brighton")
            XCTAssertEqual(FileMetadata.read(url).caption, "Rex at Brighton", ext)
            var read = try FileMetadataWriter.write(.init(caption: short), to: url)
            XCTAssertEqual(read.caption, short, ext)
            read = try FileMetadataWriter.write(.init(caption: "Two dogs"), to: url)
            XCTAssertEqual(read.caption, "Two dogs", ext)
            read = try FileMetadataWriter.write(.init(caption: ""), to: url)
            // With no caption left, the title stands in, as when reading any file.
            XCTAssertEqual(read.caption, "Original title", ext)
        }
    }

    private func title(_ url: URL) -> String? {
        CGImageSourceCreateWithURL(url as CFURL, nil).flatMap { CGImageSourceCopyMetadataAtIndex($0, 0, nil) }
            .flatMap { FileMetadata.xmpString($0, "dc:title") }
    }

    func testTheTitleFollowsTheCaptionButARealTitleIsKept() throws {
        for (ext, type) in Self.formats {
            // A title someone wrote stays as it is.
            let titled = try makePhoto("titled.\(ext)", type: type)
            try FileMetadataWriter.write(.init(caption: "Two dogs"), to: titled)
            XCTAssertEqual(title(titled), "Original title", ext)
            // No title: it follows the caption, so File Explorer's Title shows it too (#62).
            let untitled = try makePhoto("untitled.\(ext)", type: type, title: nil)
            try FileMetadataWriter.write(.init(caption: "Two dogs"), to: untitled)
            XCTAssertEqual(title(untitled), "Two dogs", ext)
            // A title that was the caption (as the Windows app wrote) keeps following it.
            let windows = try makePhoto("windows.\(ext)", type: type, caption: "Rex", title: "Rex")
            try FileMetadataWriter.write(.init(caption: "Rex on the beach"), to: windows)
            XCTAssertEqual(title(windows), "Rex on the beach", ext)
        }
    }

    func testACaptionWrittenOnlyInTheTIFFDescriptionIsReplaced() throws {
        for (ext, type) in Self.formats {
            let url = temp.appendingPathComponent("described.\(ext)")
            let context = try XCTUnwrap(CGContext(data: nil, width: 8, height: 8, bitsPerComponent: 8, bytesPerRow: 0,
                                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            let destination = try XCTUnwrap(CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, 1, nil))
            let properties = [kCGImagePropertyTIFFDictionary: [kCGImagePropertyTIFFImageDescription: "From the camera"]]
            CGImageDestinationAddImage(destination, try XCTUnwrap(context.makeImage()), properties as CFDictionary)
            XCTAssertTrue(CGImageDestinationFinalize(destination))
            XCTAssertEqual(FileMetadata.read(url).caption, "From the camera", ext)
            let result = try FileMetadataWriter.write(.init(caption: short), to: url)
            XCTAssertEqual(result.caption, short, ext)
        }
    }

    func testUnicodeAndMarkupCharactersSurvive() throws {
        let url = try makePhoto("photo.jpg", type: .jpeg)
        let text = "Ünïcödé café, \"quotes\" & <angle> brackets.\nA second line."
        let read = try FileMetadataWriter.write(.init(caption: text, accessibilityDescription: text), to: url)
        XCTAssertEqual(read.caption, text)
        XCTAssertEqual(read.altText, text)
    }

    // MARK: - Refusing

    func testGIFAndOtherFormatsAreRefusedAndLeftAlone() throws {
        let gif = try makePhoto("anim.gif", type: .gif)
        let bytes = try Data(contentsOf: gif)
        XCTAssertThrowsError(try FileMetadataWriter.write(.init(caption: short), to: gif)) { error in
            XCTAssertEqual(error as? FileMetadataWriteError, .unsupportedFormat("GIF"))
            XCTAssertEqual((error as? LocalizedError)?.errorDescription, "GIF files can't hold captions or descriptions.")
        }
        XCTAssertEqual(try Data(contentsOf: gif), bytes)

        XCTAssertFalse(FileMetadataWriter.canWrite(name: "a.webp"))
        XCTAssertFalse(FileMetadataWriter.canWrite(name: "a.NEF"))
        XCTAssertTrue(FileMetadataWriter.canWrite(name: "IMG_1.HEIC"))
        XCTAssertEqual(FileMetadataWriter.formatName(of: "DSC_1.NEF"), "RAW")
        XCTAssertEqual(FileMetadataWriter.formatName(of: "a.webp"), "WEBP")
    }

    func testPNGsAreWrittenByTheirOwnWriter() throws {
        // ImageIO's copy damaged a PNG's EXIF UserComment (#61); PNGMetadata edits the chunks instead
        // (PNGMetadataTests covers it in depth).
        XCTAssertTrue(FileMetadataWriter.canWrite(name: "Screenshot.PNG"))
        let url = try makePhoto("shot.png", type: .png)
        let read = try FileMetadataWriter.write(.init(caption: "A dog", accessibilityDescription: "A dog"), to: url)
        XCTAssertEqual(read.caption, "A dog")
        XCTAssertEqual(read.altText, "A dog")
    }

    /// A JPEG with something after its picture, as a motion photo keeps its video, or with an MPF segment as Ultra
    /// HDR has (#79).
    private func jpeg(trailer: Data? = nil, mpf: Bool = false) throws -> URL {
        let url = try makePhoto("motion.jpg", type: .jpeg)
        var bytes = try Data(contentsOf: url)
        if mpf {
            // APP2 "MPF\0" with a minimal big-endian TIFF header, right after SOI.
            let body: [UInt8] = Array("MPF".utf8) + [0] + [0x4D, 0x4D, 0, 42, 0, 0, 0, 8, 0, 0]
            let segment: [UInt8] = [0xFF, 0xE2, 0, UInt8(body.count + 2)] + body
            bytes.insert(contentsOf: segment, at: 2)
        }
        if let trailer { bytes.append(trailer) }
        try bytes.write(to: url)
        return url
    }

    func testAMotionPhotosVideoAfterThePictureComesThroughExactly() throws {
        let video = Data("ftypmp42 pretend motion photo video ".utf8) + Data(repeating: 0xAB, count: 4096)
        let url = try jpeg(trailer: video)
        XCTAssertEqual(JPEGLayout.trailingData(try Data(contentsOf: url)), video)
        let read = try FileMetadataWriter.write(.init(caption: "Two dogs", accessibilityDescription: "Two dogs."), to: url)
        XCTAssertEqual(read.caption, "Two dogs")
        XCTAssertEqual(JPEGLayout.trailingData(try Data(contentsOf: url)), video, "the video is byte-for-byte the same")
    }

    /// An MPF segment with no MP Entries can't be patched (#90): JPEGMetadataTests covers the ones that can.
    func testAMultiPictureJPEGWhoseIndexCantBeFollowedIsRefusedAndLeftAlone() throws {
        let url = try jpeg(mpf: true)
        let before = try Data(contentsOf: url)
        XCTAssertTrue(JPEGLayout.hasMultiPicture(before))
        XCTAssertThrowsError(try FileMetadataWriter.write(.init(caption: "A dog"), to: url)) { error in
            XCTAssertEqual(error as? FileMetadataWriteError, .multiPicture)
        }
        XCTAssertEqual(try Data(contentsOf: url), before)
    }

    func testAnOrdinaryJPEGHasNoTrailerAndOnePicture() throws {
        let bytes = try Data(contentsOf: try makePhoto("plain.jpg", type: .jpeg))
        XCTAssertFalse(JPEGLayout.hasMultiPicture(bytes))
        XCTAssertEqual(JPEGLayout.trailingData(bytes), Data())
        XCTAssertNil(JPEGLayout.trailingData(Data([0x89, 0x50, 0x4E, 0x47])), "not a JPEG")
    }

    func testAGIFNamedLikeAJPEGIsRefusedToo() throws {
        let gif = try makePhoto("anim.gif", type: .gif)
        let renamed = temp.appendingPathComponent("not-really.jpg")
        try FileManager.default.moveItem(at: gif, to: renamed)
        XCTAssertThrowsError(try FileMetadataWriter.write(.init(caption: short), to: renamed)) { error in
            XCTAssertEqual(error as? FileMetadataWriteError, .unsupportedFormat("GIF"))
        }
    }

    // MARK: - Changed while it's saved (#111)

    /// Another app writing into the file after it was read: the new copy was made from the old bytes, so it isn't
    /// swapped in, and the file is left as the other app left it, with no copy left behind.
    func testAFileChangedWhileItsSavedIsLeftAsTheOtherAppLeftIt() throws {
        for (ext, type) in Self.formats {
            let url = try makePhoto("changed.\(ext)", type: type)
            var theirs = Data()
            FileMetadataWriter.beforeReplacing = { url in
                // Appended to in place, as an editor saving over it might, keeping its inode.
                guard let handle = try? FileHandle(forWritingTo: url) else { return }
                handle.seekToEndOfFile()
                handle.write(Data("more".utf8))
                try? handle.close()
                theirs = (try? Data(contentsOf: url)) ?? Data()
            }
            XCTAssertThrowsError(try FileMetadataWriter.write(.init(caption: short), to: url), ext) { error in
                XCTAssertEqual(error as? FileMetadataWriteError, FileMetadataWriter.changedWhileSaving, ext)
            }
            XCTAssertFalse(theirs.isEmpty, ext)
            XCTAssertEqual(try Data(contentsOf: url), theirs, ext)
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: temp.path), ["changed.\(ext)"], ext)
            try FileManager.default.removeItem(at: url)
        }
    }

    /// Another file put in its place, the same size with the same modified time: still refused, being a different file.
    func testAFileReplacedWhileItsSavedIsLeftAlone() throws {
        let url = try makePhoto("replaced.jpg", type: .jpeg)
        var theirs = Data()
        FileMetadataWriter.beforeReplacing = { url in
            let other = url.deletingLastPathComponent().appendingPathComponent("other.tmp")
            guard var bytes = try? Data(contentsOf: url),
                  let date = try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate else { return }
            bytes[bytes.count - 3] ^= 0xFF
            try? bytes.write(to: other)
            try? FileManager.default.setAttributes([.modificationDate: date], ofItemAtPath: other.path)
            _ = rename(other.path, url.path)
            theirs = bytes
        }
        XCTAssertThrowsError(try FileMetadataWriter.write(.init(caption: short), to: url)) {
            XCTAssertEqual($0 as? FileMetadataWriteError, FileMetadataWriter.changedWhileSaving)
        }
        XCTAssertFalse(theirs.isEmpty)
        XCTAssertEqual(try Data(contentsOf: url), theirs)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: temp.path), ["replaced.jpg"])
    }

    /// Nothing changed it: written as usual, the hook seeing the original still there.
    func testAFileNobodyElseTouchesIsWritten() throws {
        let url = try makePhoto("untouched.png", type: .png)
        var called = false
        FileMetadataWriter.beforeReplacing = { _ in called = true }
        XCTAssertEqual(try FileMetadataWriter.write(.init(caption: short), to: url).caption, short)
        XCTAssertTrue(called)
    }

    // MARK: - Alt text

    func testAShortDescriptionIsItsOwnAltText() {
        XCTAssertEqual(AccessibilityText.altText(from: "  \(short)\n"), short)
        XCTAssertNil(AccessibilityText.extendedDescription(from: short))
        let exactly250 = String(repeating: "a", count: 249) + "."
        XCTAssertEqual(AccessibilityText.altText(from: exactly250), exactly250)
    }

    func testALongDescriptionKeepsTheWholeSentencesThatFit() {
        let first = "A brown dog with floppy ears runs along a sandy beach at low tide, chasing a red ball."  // 86
        let second = "Waves roll in behind it under a pale blue sky with a few thin clouds near the horizon."
        let third = "In the distance a lighthouse stands on a rocky point, and two people walk a black dog by the water."
        let text = [first, second, third].joined(separator: " ")
        XCTAssertGreaterThan(text.count, 250)
        XCTAssertEqual(AccessibilityText.altText(from: text), first + " " + second)
        XCTAssertEqual(AccessibilityText.extendedDescription(from: text), text)
    }

    func testOneLongSentenceIsCutAtAWordWithAnEllipsis() {
        let words = Array(repeating: "seaside,", count: 40).joined(separator: " ") + " end."
        let alt = AccessibilityText.altText(from: words)
        XCTAssertLessThanOrEqual(alt.count, 250)
        XCTAssertTrue(alt.hasSuffix("seaside…"), alt)
        XCTAssertFalse(alt.contains("sea…"))
        XCTAssertEqual(AccessibilityText.extendedDescription(from: words), words)
    }

    func testOneEndlessWordIsCutWhereItMustBe() {
        let word = String(repeating: "x", count: 400)
        let alt = AccessibilityText.altText(from: word)
        XCTAssertEqual(alt.count, 250)
        XCTAssertTrue(alt.hasSuffix("x…"))
    }

    // MARK: - Keeping the description

    func testAWrittenFilesDescriptionMovesToItsNewKeyWithItsEditedMark() throws {
        let url = try makePhoto("photo.jpg", type: .jpeg)
        let before = try XCTUnwrap(FolderScanner.file(at: url.path))
        let cache = DescriptionCache(fileURL: temp.appendingPathComponent("store/Descriptions.json"))
        cache.remember("Rex on the beach", forKey: before.descriptionKey, edited: true)

        try FileMetadataWriter.write(.init(caption: "Rex on the beach", accessibilityDescription: "Rex on the beach"), to: url)
        let after = try XCTUnwrap(FolderScanner.file(at: url.path))
        XCTAssertNotEqual(after.size, before.size, "writing changes the size, which is part of the key")
        XCTAssertEqual(after.modified, before.modified)

        cache.rekey(from: before.descriptionKey, to: after.descriptionKey)
        XCTAssertEqual(cache.description(forKey: after.descriptionKey), "Rex on the beach")
        XCTAssertTrue(cache.isEdited(forKey: after.descriptionKey))
        XCTAssertNil(cache.description(forKey: before.descriptionKey))
        // Nothing saved under the old key: nothing to move.
        cache.rekey(from: "file:/NOWHERE|1|1", to: "file:/ELSEWHERE|1|1")
        XCTAssertNil(cache.description(forKey: "file:/ELSEWHERE|1|1"))
    }

    // MARK: - What's said

    func testWhatsSaidNamesOneSkippedFileAndCountsSeveral() {
        XCTAssertEqual(FileWriteSummary.text(done: "Saved to the file", skipped: [], notDescribed: 0), "Saved to the file")
        XCTAssertEqual(FileWriteSummary.text(done: "Saved to 2 files", skipped: [("anim.gif", "GIF")], notDescribed: 1),
                       "Saved to 2 files. Skipped anim.gif: GIF files can't hold captions or descriptions. 1 photo hasn't been described yet")
        XCTAssertEqual(FileWriteSummary.text(done: nil, skipped: [("a.gif", "GIF"), ("b.webp", "WEBP"), ("c.gif", "GIF")], notDescribed: 2),
                       "Skipped 3 files: GIF and WEBP files can't hold captions or descriptions. 2 photos haven't been described yet")
    }

    func testAPhotoWhoseSecondPictureCantBeKeptRightIsNamed() {
        // Worded as Windows says it (#90, #150).
        XCTAssertEqual(FileWriteSummary.text(done: "Saved to 2 files", skipped: [], secondPicture: ["hdr.jpg"], notDescribed: 0),
                       "Saved to 2 files. hdr.jpg holds a second picture, such as an HDR map, laid out in a way that can't be updated safely, so it was left as it is")
        XCTAssertEqual(FileWriteSummary.text(done: nil, skipped: [], secondPicture: ["a.jpg", "b.jpg"], notDescribed: 0),
                       "2 files hold a second picture, such as an HDR map, laid out in a way that can't be updated safely, so they were left as they are")
    }

    func testUnsupportedFilesAreFoundByName() {
        let files = ["a.jpg", "b.gif", "c.NEF", "d.heic"].map {
            MediaFile(path: "/Pictures/\($0)", size: 1, created: nil, modified: .distantPast)
        }
        let unsupported = FileWriteSummary.unsupported(files)
        XCTAssertEqual(unsupported.map(\.name), ["b.gif", "c.NEF"])
        XCTAssertEqual(unsupported.map(\.format), ["GIF", "RAW"])
    }
}
