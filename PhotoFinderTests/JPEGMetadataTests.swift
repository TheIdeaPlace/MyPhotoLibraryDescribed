import CoreVideo
import CryptoKit
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import Photo_Finder

/// Writing into JPEG files by editing their segments (#81). The JPEGs are built segment by segment like a camera's:
/// JFIF, EXIF with a description, a maker note and an embedded thumbnail, XMP as ExifTool writes it, Photoshop's IPTC
/// with its digest, and a comment, then the picture ImageIO compressed, and sometimes a motion photo's video after it.
final class JPEGMetadataTests: XCTestCase {
    private var temp: URL!

    override func setUpWithError() throws {
        // These tests write into files, so changes are allowed while they run (#84); the app starts read-only.
        PhotoChanges.allowed = true
        temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        PhotoChanges.allowed = false
        try? FileManager.default.removeItem(at: temp)
    }

    // MARK: - Building JPEGs

    private static let width = 32, height = 16
    private static let dateTaken = "2024:06:01 10:11:12"
    private static let make = "TestCam"
    /// A maker note like a camera's: opaque bytes no reader is meant to change.
    private static let makerNote: [UInt8] = Array("Nikon".utf8) + [0, 2, 0x10, 0, 0] + (0..<2000).map { UInt8($0 % 251) }
    private static let comment = Array("Made by a camera".utf8)
    private static let video: [UInt8] = Array("ftypmp42 pretend motion photo video ".utf8) + [UInt8](repeating: 0xAB, count: 4096)

    private func u16(_ v: Int, _ big: Bool) -> [UInt8] {
        big ? [UInt8(v >> 8), UInt8(v & 0xFF)] : [UInt8(v & 0xFF), UInt8(v >> 8)]
    }

    private func u32(_ v: Int, _ big: Bool) -> [UInt8] {
        let b = [UInt8((v >> 24) & 0xFF), UInt8((v >> 16) & 0xFF), UInt8((v >> 8) & 0xFF), UInt8(v & 0xFF)]
        return big ? b : b.reversed()
    }

    private func segment(_ marker: UInt8, _ body: [UInt8]) -> [UInt8] {
        [0xFF, marker] + u16(body.count + 2, true) + body
    }

    /// A JPEG of a gradient made by ImageIO, with nothing but the picture.
    private func encoded(width: Int, height: Int) throws -> [UInt8] {
        let context = try XCTUnwrap(CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                              space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        for x in 0..<width {
            context.setFillColor(red: CGFloat(x) / CGFloat(width), green: 0.3, blue: 0.6, alpha: 1)
            context.fill(CGRect(x: x, y: 0, width: 1, height: height))
        }
        let data = NSMutableData()
        let destination = try XCTUnwrap(CGImageDestinationCreateWithData(data, UTType.jpeg.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, try XCTUnwrap(context.makeImage()), nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        return [UInt8](data as Data)
    }

    /// The picture's own segments (tables, frame header, scan and end), without any APPn ImageIO added.
    private func pictureSegments(_ jpeg: [UInt8]) -> [UInt8] {
        var i = 2
        while (0xE0...0xEF).contains(jpeg[i + 1]) { i += 2 + (Int(jpeg[i + 2]) << 8 | Int(jpeg[i + 3])) }
        return Array(jpeg[i...])
    }

    private func entry(_ tag: Int, _ type: Int, _ count: Int, _ value: [UInt8], _ big: Bool) -> [UInt8] {
        u16(tag, big) + u16(type, big) + u32(count, big) + value + [UInt8](repeating: 0, count: 4 - value.count)
    }

    /// TIFF data like a camera's: IFD0 with the description (when given), Make, Orientation and the EXIF pointer; the
    /// EXIF directory with DateTimeOriginal and a maker note; IFD1 with the embedded thumbnail.
    private func exif(big: Bool, description: String?, thumbnail: [UInt8]) -> [UInt8] {
        let ifd0Count = description == nil ? 3 : 4
        var text: [UInt8] = description.map { Array($0.utf8) + [0] } ?? []
        if text.count % 2 == 1 { text.append(0) }
        let makeText = Array(Self.make.utf8) + [0]
        let date = Array(Self.dateTaken.utf8) + [0]
        let ifd0End = 8 + 2 + ifd0Count * 12 + 4
        let makeAt = ifd0End + text.count
        let exifIFD = makeAt + makeText.count
        let dateAt = exifIFD + 2 + 2 * 12 + 4
        let noteAt = dateAt + date.count
        var ifd1 = noteAt + Self.makerNote.count
        let notePad = ifd1 % 2
        ifd1 += notePad
        let thumbAt = ifd1 + 2 + 3 * 12 + 4

        var b = (big ? Array("MM".utf8) : Array("II".utf8)) + u16(42, big) + u32(8, big)
        b += u16(ifd0Count, big)
        if let description { b += entry(0x010E, 2, description.utf8.count + 1, u32(ifd0End, big), big) }
        b += entry(0x010F, 2, makeText.count, u32(makeAt, big), big)
        b += entry(0x0112, 3, 1, u16(1, big), big)
        b += entry(0x8769, 4, 1, u32(exifIFD, big), big)
        b += u32(ifd1, big)
        b += text + makeText
        XCTAssertEqual(b.count, exifIFD)
        b += u16(2, big)
        b += entry(0x9003, 2, date.count, u32(dateAt, big), big)
        b += entry(0x927C, 7, Self.makerNote.count, u32(noteAt, big), big)
        b += u32(0, big)
        b += date + Self.makerNote + [UInt8](repeating: 0, count: notePad)
        XCTAssertEqual(b.count, ifd1)
        b += u16(3, big)
        b += entry(0x0103, 3, 1, u16(6, big), big)
        b += entry(0x0201, 4, 1, u32(thumbAt, big), big)
        b += entry(0x0202, 4, 1, u32(thumbnail.count, big), big)
        b += u32(0, big)
        b += thumbnail
        return b
    }

    /// XMP as ExifTool writes it, with keywords, a creator tool and a rating alongside the caption.
    private func exifToolXMP(caption: String? = "Camera caption", title: String? = nil) -> String {
        """
        <?xpacket begin='\u{FEFF}' id='W5M0MpCehiHzreSzNTczkc9d'?>
        <x:xmpmeta xmlns:x='adobe:ns:meta/' x:xmptk='Image::ExifTool 12.76'>
        <rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>
         <rdf:Description rdf:about=''
          xmlns:dc='http://purl.org/dc/elements/1.1/'>
        \(caption.map { "  <dc:description>\n   <rdf:Alt>\n    <rdf:li xml:lang='x-default'>\($0)</rdf:li>\n   </rdf:Alt>\n  </dc:description>" } ?? "")
          <dc:subject>
           <rdf:Bag>
            <rdf:li>beach</rdf:li>
            <rdf:li>dog</rdf:li>
           </rdf:Bag>
          </dc:subject>
        \(title.map { "  <dc:title>\n   <rdf:Alt>\n    <rdf:li xml:lang='x-default'>\($0)</rdf:li>\n   </rdf:Alt>\n  </dc:title>" } ?? "")
         </rdf:Description>
         <rdf:Description rdf:about=''
          xmlns:xmp='http://ns.adobe.com/xap/1.0/'>
          <xmp:CreatorTool>ExifTool test</xmp:CreatorTool>
          <xmp:Rating>3</xmp:Rating>
         </rdf:Description>
        </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end='w'?>
        """
    }

    private func xmpSegment(_ packet: String) -> [UInt8] {
        segment(0xE1, JPEGMetadata.xmpHeader + Array(packet.utf8))
    }

    /// IPTC datasets: (record, number, text).
    private func iim(_ datasets: [(UInt8, UInt8, [UInt8])]) -> [UInt8] {
        datasets.flatMap { [0x1C, $0.0, $0.1] + u16($0.2.count, true) + $0.2 }
    }

    private func resource(_ id: Int, _ data: [UInt8]) -> [UInt8] {
        Array("8BIM".utf8) + u16(id, true) + [0, 0] + u32(data.count, true) + data + (data.count % 2 == 1 ? [0] : [])
    }

    /// Photoshop's segment: a resolution resource, the IPTC, and its digest (an MD5 of the IPTC) when `digest`.
    private func photoshopSegment(_ iptc: [UInt8], digest: [UInt8]? = nil) -> [UInt8] {
        let resolution: [UInt8] = [0, 72, 0, 0, 0, 1, 0, 1, 0, 72, 0, 0, 0, 1, 0, 1]
        var body = JPEGMetadata.photoshopHeader + resource(0x03ED, resolution) + resource(0x0404, iptc)
        if let digest { body += resource(0x0425, digest) }
        return segment(0xED, body)
    }

    private static let utf8: [UInt8] = [0x1B, 0x25, 0x47]

    private func standardIPTC(caption: String? = "Camera caption", title: String? = nil, utf8: Bool = true) -> [UInt8] {
        var sets: [(UInt8, UInt8, [UInt8])] = []
        if utf8 { sets.append((1, 90, Self.utf8)) }
        sets.append((2, 0, [0, 4]))
        if let title { sets.append((2, 5, Array(title.utf8))) }
        sets += [(2, 25, Array("beach".utf8)), (2, 25, Array("dog".utf8))]
        if let caption { sets.append((2, 120, Array(caption.utf8))) }
        sets.append((2, 80, Array("A. Photographer".utf8)))
        return iim(sets)
    }

    private var thumbnail: [UInt8] = []

    /// A camera's JPEG, segment by segment.
    @discardableResult
    private func makeJPEG(_ name: String = "camera.jpg", big: Bool = true, description: String? = "Camera caption",
                          xmp: String?? = .some(nil), iptc: [UInt8]? = nil, digest: Bool = true,
                          trailer: Bool = false, extra: [[UInt8]] = []) throws -> URL {
        let url = temp.appendingPathComponent(name)
        if thumbnail.isEmpty { thumbnail = try encoded(width: 8, height: 4) }
        let picture = pictureSegments(try encoded(width: Self.width, height: Self.height))
        let jfif: [UInt8] = Array("JFIF".utf8) + [0, 1, 1, 0, 0, 72, 0, 72, 0, 0]
        var bytes: [UInt8] = [0xFF, 0xD8] + segment(0xE0, jfif)
        bytes += segment(0xE1, JPEGMetadata.exifHeader + exif(big: big, description: description, thumbnail: thumbnail))
        // XMP as ExifTool writes it unless told otherwise; .some(nil) for the default, .none for no XMP.
        switch xmp {
        case .none: break
        case .some(.none): bytes += xmpSegment(exifToolXMP())
        case .some(.some(let packet)): bytes += xmpSegment(packet)
        }
        let block = iptc ?? standardIPTC()
        bytes += photoshopSegment(block, digest: digest ? Array(Insecure.MD5.hash(data: block)) : nil)
        bytes += segment(0xFE, Self.comment)
        for segment in extra { bytes += segment }
        bytes += picture
        if trailer { bytes += Self.video }
        try Data(bytes).write(to: url)
        let old = Date(timeIntervalSince1970: 1_700_000_000.123456)
        try FileManager.default.setAttributes([.modificationDate: old, .creationDate: old.addingTimeInterval(-60)],
                                              ofItemAtPath: url.path)
        return url
    }

    // MARK: - Reading what was written

    private func segments(_ url: URL) throws -> [JPEGMetadata.Segment] {
        try JPEGMetadata.segments(of: Data(contentsOf: url)).segments
    }

    private func picture(_ url: URL) throws -> [UInt8] {
        try JPEGMetadata.segments(of: Data(contentsOf: url)).picture
    }

    /// Every segment but the ones allowed to change: the XMP, and the EXIF (in place, the same size).
    private func untouchedSegments(_ url: URL) throws -> [JPEGMetadata.Segment] {
        try segments(url).filter { !JPEGMetadata.isXMP($0) && !JPEGMetadata.isEXIF($0) }
    }

    private func exifBody(_ url: URL) throws -> [UInt8] {
        Array(try XCTUnwrap(segments(url).first(where: JPEGMetadata.isEXIF)).body.dropFirst(6))
    }

    private func xmpPacket(_ url: URL) throws -> String {
        let segment = try XCTUnwrap(segments(url).first(where: JPEGMetadata.isXMP))
        return String(decoding: segment.body.dropFirst(JPEGMetadata.xmpHeader.count), as: UTF8.self)
    }

    private func photoshop(_ url: URL) throws -> JPEGMetadata.Segment {
        try XCTUnwrap(segments(url).first(where: JPEGMetadata.isPhotoshop))
    }

    /// Where the file's own first segment with `marker` starts, its picture's start of scan included (the EXIF
    /// thumbnail has markers of its own, so searching the bytes won't do).
    private func offset(of marker: UInt8, in bytes: [UInt8]) throws -> Int? {
        let (segments, picture) = try JPEGMetadata.segments(of: Data(bytes))
        if marker == 0xDA { return bytes.count - picture.count }
        var position = 2
        for segment in segments {
            if segment.marker == marker { return position }
            position += segment.raw.count
        }
        return nil
    }

    private func properties(_ url: URL) throws -> [CFString: Any] {
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
        return try XCTUnwrap(CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any])
    }

    private func xmpValue(_ url: URL, _ path: String) -> String? {
        CGImageSourceCreateWithURL(url as CFURL, nil).flatMap { CGImageSourceCopyMetadataAtIndex($0, 0, nil) }
            .flatMap { FileMetadata.xmpString($0, path) }
    }

    private func pixels(_ url: URL) -> Data? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return nil }
        return image.dataProvider?.data as Data?
    }

    /// The embedded EXIF thumbnail, only if it's there: never made from the picture.
    private func embeddedThumbnail(_ url: URL) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let options: [CFString: Any] = [kCGImageSourceCreateThumbnailFromImageIfAbsent: false,
                                        kCGImageSourceCreateThumbnailFromImageAlways: false]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }

    private func modified(_ url: URL) -> timespec {
        var info = stat()
        stat(url.path, &info)
        return info.st_mtimespec
    }

    /// Asserts the write is refused and the folder holds the file exactly as it was, with no copy left behind.
    private func assertRefused(_ changes: FileMetadataChanges, _ url: URL, file: StaticString = #filePath, line: UInt = #line) throws {
        let before = try Data(contentsOf: url)
        let names = try FileManager.default.contentsOfDirectory(atPath: temp.path).sorted()
        XCTAssertThrowsError(try FileMetadataWriter.write(changes, to: url), file: file, line: line)
        XCTAssertEqual(try Data(contentsOf: url), before, file: file, line: line)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: temp.path).sorted(), names, file: file, line: line)
    }

    private let short = "A brown dog runs on a sandy beach."

    // MARK: - A camera's JPEG

    func testACameraJPEGGetsItsCaptionAndAltTextAndKeepsEverythingElse() throws {
        for big in [true, false] {
            let label = big ? "big-endian" : "little-endian"
            let url = try makeJPEG("camera-\(label).jpg", big: big, trailer: true)
            let before = FileMetadata.read(url)
            XCTAssertEqual(before.caption, "Camera caption", label)
            let untouched = try untouchedSegments(url)
            let pictureBefore = try picture(url)
            let exifBefore = try exifBody(url)
            let pixelsBefore = try XCTUnwrap(pixels(url))
            let thumbnailBefore = try XCTUnwrap(embeddedThumbnail(url), "\(label): the test file has an EXIF thumbnail")
            let date = modified(url)
            let created = try url.resourceValues(forKeys: [.creationDateKey]).creationDate

            let after = try FileMetadataWriter.write(.init(caption: short, accessibilityDescription: short), to: url)

            XCTAssertEqual(after.caption, short, label)
            XCTAssertEqual(after.altText, short, label)
            XCTAssertEqual(after.extendedDescription, "", label)
            XCTAssertEqual(after.keywords, ["beach", "dog"], label)
            XCTAssertEqual(after.rating, 3, label)
            XCTAssertNotNil(after.dateTaken, label)
            XCTAssertEqual(after.dateTaken, before.dateTaken, label)
            XCTAssertEqual(xmpValue(url, "xmp:CreatorTool"), "ExifTool test", label)
            XCTAssertEqual(xmpValue(url, "dc:title"), short, "\(label): an empty title follows the caption")
            let tiff = try properties(url)[kCGImagePropertyTIFFDictionary] as? [CFString: Any]
            XCTAssertEqual(tiff?[kCGImagePropertyTIFFMake] as? String, Self.make, label)

            // The EXIF: the same size, its description entry gone, the maker note and thumbnail where they were.
            let exifAfter = try exifBody(url)
            XCTAssertEqual(exifAfter.count, exifBefore.count, label)
            XCTAssertNil(try PNGMetadata.removingImageDescription(fromEXIF: exifAfter), "\(label): the EXIF description is gone")
            XCTAssertNil(Data(exifAfter).range(of: Data("Camera caption".utf8)), "\(label): the old text is blanked")
            let ifd0End = 8 + 2 + 4 * 12 + 4
            let descriptionText = Array("Camera caption".utf8).count + 2  // with its zero, padded to even
            XCTAssertEqual(Array(exifAfter[(ifd0End + descriptionText)...]), Array(exifBefore[(ifd0End + descriptionText)...]),
                           "\(label): everything after the description's text is as it was")
            XCTAssertNotNil(Data(exifAfter).range(of: Data(Self.makerNote)), "\(label): the maker note is kept")
            let thumbnailAfter = try XCTUnwrap(embeddedThumbnail(url), "\(label): the EXIF thumbnail is kept")
            XCTAssertEqual(thumbnailAfter.width, thumbnailBefore.width, label)
            XCTAssertEqual(thumbnailAfter.dataProvider?.data as Data?, thumbnailBefore.dataProvider?.data as Data?, label)

            // Every other segment byte for byte (the comment and Photoshop's IPTC with its digest included), the
            // picture data and the video after it.
            XCTAssertEqual(try untouchedSegments(url), untouched, label)
            XCTAssertTrue(try untouchedSegments(url).contains { $0.marker == 0xFE && Array($0.body) == Self.comment }, label)
            XCTAssertTrue(try untouchedSegments(url).contains(where: JPEGMetadata.isPhotoshop), label)
            XCTAssertEqual(try picture(url), pictureBefore, label)
            XCTAssertEqual(JPEGLayout.trailingData(try Data(contentsOf: url)), Data(Self.video), label)
            XCTAssertEqual(pixels(url), pixelsBefore, "\(label): the picture changed")
            // The dates as they were.
            let now = modified(url)
            XCTAssertEqual(now.tv_sec, date.tv_sec, label)
            XCTAssertEqual(now.tv_nsec, date.tv_nsec, label)
            XCTAssertEqual(try URL(fileURLWithPath: url.path).resourceValues(forKeys: [.creationDateKey]).creationDate, created, label)
            // One XMP segment, where it was, padded for next time.
            let all = try segments(url)
            XCTAssertEqual(all.indices.filter { JPEGMetadata.isXMP(all[$0]) }, [2], label)
            let packet = try xmpPacket(url)
            XCTAssertTrue(packet.hasPrefix("<?xpacket begin="), label)
            XCTAssertTrue(packet.hasSuffix("<?xpacket end=\"w\"?>"), label)
            XCTAssertTrue(packet.contains(String(repeating: " ", count: 90)), "\(label): room to grow")
        }
    }

    func testOnlyTheMetadataSegmentsDifferAndNoTemporaryFileIsLeft() throws {
        let url = try makeJPEG()
        let before = try segments(url)
        try FileMetadataWriter.write(.init(caption: short), to: url)
        let after = try segments(url)
        XCTAssertEqual(after.map(\.marker), before.map(\.marker))
        for (a, b) in zip(before, after) where !JPEGMetadata.isXMP(a) {
            if JPEGMetadata.isEXIF(a) { XCTAssertEqual(a.raw.count, b.raw.count) } else { XCTAssertEqual(a, b) }
        }
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: temp.path), ["camera.jpg"])
    }

    func testAltTextAloneChangesOnlyTheXMP() throws {
        let url = try makeJPEG()
        let before = try segments(url)
        let read = try FileMetadataWriter.write(.init(accessibilityDescription: short), to: url)
        XCTAssertEqual(read.altText, short)
        XCTAssertEqual(read.caption, "Camera caption")
        let after = try segments(url)
        XCTAssertEqual(after.count, before.count)
        for (a, b) in zip(before, after) where !JPEGMetadata.isXMP(a) { XCTAssertEqual(a, b) }
    }

    func testASecondWriteReplacesTheFirstAndAnEmptyCaptionRemovesIt() throws {
        let url = try makeJPEG()
        let photoshopBefore = try photoshop(url)
        var read = try FileMetadataWriter.write(.init(caption: short, accessibilityDescription: short), to: url)
        XCTAssertEqual(read.caption, short)
        read = try FileMetadataWriter.write(.init(caption: "Two dogs", accessibilityDescription: "Two dogs"), to: url)
        XCTAssertEqual(read.caption, "Two dogs")
        XCTAssertEqual(read.altText, "Two dogs")
        read = try FileMetadataWriter.write(.init(caption: ""), to: url)
        XCTAssertEqual(read.caption, "")
        // The IPTC still has the camera's caption, so the XMP keeps an empty one, which readers take as none,
        // rather than letting the IPTC's show through.
        XCTAssertEqual(xmpValue(url, "dc:description"), "")
        XCTAssertEqual(xmpValue(url, "dc:title") ?? "", "", "the title followed the caption, so it goes too")
        XCTAssertEqual(try photoshop(url), photoshopBefore, "the IPTC is never changed")
        XCTAssertEqual(read.altText, "Two dogs", "the alt text is left alone")
        XCTAssertEqual(read.keywords, ["beach", "dog"])
        XCTAssertNotNil(embeddedThumbnail(url))
    }

    func testRemovingACaptionFromAFileWithoutIPTCRemovesItFromTheXMP() throws {
        let url = try makeJPEG(iptc: iim([(1, 90, Self.utf8)]), extra: [])
        // Without an IPTC block at all.
        var bytes = [UInt8](try Data(contentsOf: url))
        let at = try XCTUnwrap(offset(of: 0xED, in: bytes))
        let length = Int(bytes[at + 2]) << 8 | Int(bytes[at + 3])
        bytes.removeSubrange(at..<(at + 2 + length))
        try Data(bytes).write(to: url)
        let read = try FileMetadataWriter.write(.init(caption: ""), to: url)
        XCTAssertEqual(read.caption, "")
        XCTAssertNil(xmpValue(url, "dc:description"))
        XCTAssertNil(try PNGMetadata.removingImageDescription(fromEXIF: exifBody(url)))
        XCTAssertEqual(read.keywords, ["beach", "dog"])
    }

    func testRemovingACaptionThatsOnlyInTheEXIFAndIPTC() throws {
        let url = try makeJPEG(xmp: exifToolXMP(caption: nil))
        XCTAssertEqual(FileMetadata.read(url).caption, "Camera caption")
        let read = try FileMetadataWriter.write(.init(caption: ""), to: url)
        XCTAssertEqual(read.caption, "")
        XCTAssertNil(try PNGMetadata.removingImageDescription(fromEXIF: exifBody(url)))
        XCTAssertEqual(read.keywords, ["beach", "dog"])
    }

    func testALongDescriptionGoesInBothFieldsAndAShortOneOrNoneClearsThem() throws {
        let url = try makeJPEG()
        let long = String(repeating: "A brown dog runs along the beach at low tide. ", count: 8).trimmingCharacters(in: .whitespaces)
        var read = try FileMetadataWriter.write(.init(accessibilityDescription: long), to: url)
        XCTAssertEqual(read.altText, AccessibilityText.altText(from: long))
        XCTAssertEqual(read.extendedDescription, long)
        read = try FileMetadataWriter.write(.init(accessibilityDescription: short), to: url)
        XCTAssertEqual(read.altText, short)
        XCTAssertEqual(read.extendedDescription, "")
        read = try FileMetadataWriter.write(.init(accessibilityDescription: ""), to: url)
        XCTAssertEqual(read.altText, "")
        XCTAssertNil(xmpValue(url, FileMetadataWriter.altTextPath))
    }

    func testTheTitleFollowsTheCaptionButARealTitleIsKept() throws {
        let titled = try makeJPEG("titled.jpg", xmp: exifToolXMP(title: "Holiday"), iptc: standardIPTC(title: "Holiday"))
        try FileMetadataWriter.write(.init(caption: "Two dogs"), to: titled)
        XCTAssertEqual(xmpValue(titled, "dc:title"), "Holiday")
        let untitled = try makeJPEG("untitled.jpg")
        try FileMetadataWriter.write(.init(caption: "Two dogs"), to: untitled)
        XCTAssertEqual(xmpValue(untitled, "dc:title"), "Two dogs")
        // A title that was the caption (as the Windows app wrote) keeps following it.
        let windows = try makeJPEG("windows.jpg", description: nil, xmp: exifToolXMP(caption: "Rex", title: "Rex"),
                                   iptc: standardIPTC(caption: "Rex", title: "Rex"))
        let read = try FileMetadataWriter.write(.init(caption: "Rex on the beach"), to: windows)
        XCTAssertEqual(read.caption, "Rex on the beach")
        XCTAssertEqual(xmpValue(windows, "dc:title"), "Rex on the beach")
    }

    /// As on Windows (#89): a caption in only one language, with no default, is replaced there, and so is any language
    /// holding the same text as the default; translations that differ are kept.
    func testACaptionInOneLanguageIsReplacedThereAndTranslationsAreKept() throws {
        func packet(_ items: [(String, String)]) -> String {
            exifToolXMP(caption: nil).replacingOccurrences(of: "  <dc:subject>", with: "  <dc:description><rdf:Alt>"
                + items.map { "<rdf:li xml:lang='\($0.0)'>\($0.1)</rdf:li>" }.joined() + "</rdf:Alt></dc:description>\n  <dc:subject>")
        }
        let single = try makeJPEG("single.jpg", description: nil, xmp: packet([("en-US", "Old words")]))
        XCTAssertEqual(FileMetadata.read(single).caption, "Old words")
        XCTAssertEqual(try FileMetadataWriter.write(.init(caption: short), to: single).caption, short)
        var written = try xmpPacket(single)
        XCTAssertFalse(written.contains("Old words"), "the only language holding the caption is replaced")
        XCTAssertTrue(written.contains("en-US"))
        XCTAssertTrue(written.contains("x-default"))

        let translated = try makeJPEG("translated.jpg", description: nil,
                                      xmp: packet([("x-default", "Old words"), ("en-GB", "Old words"), ("de", "Alte Worte")]))
        XCTAssertEqual(try FileMetadataWriter.write(.init(caption: short), to: translated).caption, short)
        written = try xmpPacket(translated)
        XCTAssertFalse(written.contains("Old words"), "the default and the language holding the same text are replaced")
        XCTAssertTrue(written.contains("Alte Worte"), "a translation that differs is kept")
    }

    func testUnicodeAndMarkupCharactersSurvive() throws {
        let url = try makeJPEG()
        let text = "Ünïcödé café, \"quotes\" & <angle> brackets.\nA second line."
        let read = try FileMetadataWriter.write(.init(caption: text, accessibilityDescription: text), to: url)
        XCTAssertEqual(read.caption, text)
        XCTAssertEqual(read.altText, text)
    }

    // MARK: - IPTC

    /// A digest that doesn't match means another app changed the IPTC after it was written, so readers (ImageIO
    /// among them) take its caption ahead of the XMP's. The IPTC is never changed, so the new caption wouldn't read
    /// back, and the file is left alone. Alt text, which IPTC-IIM doesn't have, is still written.
    func testADigestThatDoesntMatchIsRefusedForACaptionAndLeftAlone() throws {
        let block = standardIPTC()
        let url = try makeJPEG(iptc: block, digest: false)
        var bytes = [UInt8](try Data(contentsOf: url))
        let old = photoshopSegment(block), new = photoshopSegment(block, digest: [UInt8](repeating: 7, count: 16))
        let at = try XCTUnwrap(Data(bytes).range(of: Data(old))).lowerBound
        bytes.replaceSubrange(at..<(at + old.count), with: new)
        try Data(bytes).write(to: url)
        try assertRefused(.init(caption: short), url)
        XCTAssertEqual(try FileMetadataWriter.write(.init(accessibilityDescription: short), to: url).altText, short)
    }

    func testIPTCWithoutADigestIsLeftAsItIsAndTheXMPCaptionReadsBack() throws {
        let url = try makeJPEG(iptc: standardIPTC(caption: "Old IPTC caption"), digest: false)
        let before = try photoshop(url)
        XCTAssertEqual(try FileMetadataWriter.write(.init(caption: short), to: url).caption, short)
        XCTAssertEqual(try photoshop(url), before)
    }

    func testAFileWithoutIPTCOrEXIFDescriptionGetsOnlyXMP() throws {
        let url = try makeJPEG(description: nil, xmp: .none, iptc: iim([(1, 90, Self.utf8), (2, 25, Array("cat".utf8))]))
        let before = try segments(url)
        let read = try FileMetadataWriter.write(.init(caption: short, accessibilityDescription: short), to: url)
        XCTAssertEqual(read.caption, short)
        XCTAssertEqual(read.altText, short)
        let after = try segments(url)
        XCTAssertEqual(after.count, before.count + 1)
        // After JFIF and EXIF, and every other segment exactly as it was.
        XCTAssertTrue(JPEGMetadata.isXMP(after[2]))
        var rest = after
        rest.remove(at: 2)
        XCTAssertEqual(rest, before)
    }

    // MARK: - Layout

    func testDoubleQuotedXMPIsReadToo() throws {
        let url = try makeJPEG(xmp: exifToolXMP().replacingOccurrences(of: "'", with: "\""))
        let read = try FileMetadataWriter.write(.init(caption: short), to: url)
        XCTAssertEqual(read.caption, short)
        XCTAssertEqual(read.keywords, ["beach", "dog"])
    }

    func testFillBytesAndSegmentsAfterTheFrameHeaderAreKept() throws {
        let url = try makeJPEG()
        var bytes = [UInt8](try Data(contentsOf: url))
        // Fill bytes before the comment, and a second comment just before the scan.
        let com = try XCTUnwrap(offset(of: 0xFE, in: bytes))
        bytes.insert(contentsOf: [0xFF, 0xFF], at: com)
        let sos = try XCTUnwrap(offset(of: 0xDA, in: bytes))
        bytes.insert(contentsOf: segment(0xFE, Array("late comment".utf8)), at: sos)
        try Data(bytes).write(to: url)
        let before = try untouchedSegments(url)
        XCTAssertEqual(before.first { $0.marker == 0xFE }?.raw.prefix(4), [0xFF, 0xFF, 0xFF, 0xFE])
        let read = try FileMetadataWriter.write(.init(caption: short), to: url)
        XCTAssertEqual(read.caption, short)
        XCTAssertEqual(try untouchedSegments(url), before)
    }

    /// A description whose text overlaps bytes something else in the EXIF uses (here the thumbnail) loses its entry,
    /// but those bytes aren't blanked.
    func testTextTheThumbnailStillUsesIsntBlanked() throws {
        let thumb = try encoded(width: 8, height: 4)
        var tiff = exif(big: true, description: "Camera caption", thumbnail: thumb)
        // IFD1's offset follows IFD0's four entries; its second entry is the thumbnail's offset.
        let ifd1 = (58..<62).reduce(0) { $0 << 8 | Int(tiff[$1]) }
        let thumbAt = ((ifd1 + 2 + 12 + 8)..<(ifd1 + 2 + 12 + 12)).reduce(0) { $0 << 8 | Int(tiff[$1]) }
        // The description now points at the thumbnail's first 16 bytes.
        tiff.replaceSubrange(14..<22, with: u32(16, true) + u32(thumbAt, true))
        let changed = try XCTUnwrap(PNGMetadata.removingImageDescription(fromEXIF: tiff))
        XCTAssertEqual(Array(changed[thumbAt...]), Array(tiff[thumbAt...]), "the thumbnail is as it was")
        XCTAssertNil(try PNGMetadata.removingImageDescription(fromEXIF: changed))
    }

    // MARK: - Refusing

    func testExtendedXMPIsRefusedAndLeftAlone() throws {
        let extended = segment(0xE1, JPEGMetadata.extendedXMPHeader + [UInt8](repeating: 0x41, count: 40))
        try assertRefused(.init(caption: short), try makeJPEG(extra: [extended]))
    }

    func testTwoXMPSegmentsAreRefused() throws {
        try assertRefused(.init(accessibilityDescription: short), try makeJPEG(extra: [xmpSegment(exifToolXMP(caption: "Stale"))]))
    }

    func testUnreadableXMPIsRefusedAndLeftAlone() throws {
        try assertRefused(.init(caption: short), try makeJPEG(xmp: "<x:xmpmeta this is not XMP"))
    }

    func testXMPThatIsntValidUTF8IsRefusedAndLeftAlone() throws {
        let url = temp.appendingPathComponent("camera.jpg")
        try makeJPEG()
        // A Latin-1 "é" (0xE9) in the caption: not UTF-8.
        var bytes = [UInt8](try Data(contentsOf: url))
        let caption = try XCTUnwrap(Data(bytes).range(of: Data("Camera caption</rdf:li>".utf8)))
        bytes[caption.lowerBound + 1] = 0xE9
        try Data(bytes).write(to: url)
        try assertRefused(.init(caption: short), url)
        try assertRefused(.init(accessibilityDescription: short), url)
    }

    func testXMPTooLargeForOneSegmentIsRefused() throws {
        let url = try makeJPEG()
        let huge = String(repeating: "A very long description. ", count: 3000)
        XCTAssertThrowsError(try JPEGMetadata.rewrite(Data(contentsOf: url), setting: [FileMetadataWriter.captionPath: huge]))
        try assertRefused(.init(caption: huge), url)
        // Just under the limit, the padding gives way.
        let packet = try JPEGMetadata.paddedPacket([UInt8](repeating: 0x20, count: JPEGMetadata.maxBody - 200))
        XCTAssertLessThanOrEqual(packet.count, JPEGMetadata.maxBody)
    }

    func testMalformedJPEGsAreRefused() throws {
        let good = [UInt8](try Data(contentsOf: makeJPEG()))
        let sos = try XCTUnwrap(offset(of: 0xDA, in: good))
        let sof = try XCTUnwrap(offset(of: 0xC0, in: good))
        var noFrame = good
        noFrame[sof + 1] = 0xFE  // the frame header turned into a comment
        var badLength = good
        badLength[4] = 0xFF  // the JFIF segment claims to run past the end
        badLength[5] = 0xF0
        var standalone = good
        standalone.insert(contentsOf: [0xFF, 0xD0], at: 2)  // a restart marker before the picture
        let bad: [[UInt8]] = [Array(good[0..<(sos / 2)]), Array(good[0..<sos]), Array("GIF89a....".utf8), noFrame, badLength,
                              standalone, Array(good[0..<2]) + [0x00] + Array(good[2...])]
        for (index, bytes) in bad.enumerated() {
            XCTAssertThrowsError(try JPEGMetadata.rewrite(Data(bytes), setting: [FileMetadataWriter.captionPath: "x"]), "\(index)")
        }
        // Through the writer too, for a file cut short in its segments.
        let cut = temp.appendingPathComponent("cut.jpg")
        try Data(good[0..<sos]).write(to: cut)
        try assertRefused(.init(caption: short), cut)
    }

    func testAMultiPictureJPEGWhoseIndexCantBeFollowedIsRefused() throws {
        // An MPF segment with an empty directory: there's no MP Entry to patch, so the file mustn't change size.
        let body: [UInt8] = Array("MPF".utf8) + [0] + [0x4D, 0x4D, 0, 42, 0, 0, 0, 8, 0, 0]
        let url = try makeJPEG(extra: [segment(0xE2, body)])
        let before = try Data(contentsOf: url)
        XCTAssertThrowsError(try FileMetadataWriter.write(.init(caption: short), to: url)) { error in
            XCTAssertEqual(error as? FileMetadataWriteError, .multiPicture)
        }
        XCTAssertEqual(try Data(contentsOf: url), before)
    }

    // MARK: - Multi-picture files (#90)

    /// A multi-picture file as Ultra HDR and MPO cameras write it: `main` with an MPF index (CIPA DC-007) after `count`
    /// of its segments, then `second`. Its two MP Entries give the main picture's size and where the second starts,
    /// counted from the index's TIFF header. The same layout as the Windows app's tests (#150): "MPF\0" at 0 of the
    /// segment's body, the header at 4, the IFD at 8 from the header, the MP Entries at 50 from it.
    private func multiPicture(_ main: [UInt8], second: [UInt8], big: Bool, after count: Int) throws -> [UInt8] {
        var tiff: [UInt8] = (big ? [0x4D, 0x4D, 0, 0x2A] : [0x49, 0x49, 0x2A, 0]) + u32(8, big) + u16(3, big)
        tiff += entry(0xB000, 7, 4, Array("0100".utf8), big)  // MPFVersion
        tiff += entry(0xB001, 4, 1, u32(2, big), big)  // NumberOfImages
        tiff += entry(0xB002, 7, 32, u32(50, big), big)  // MPEntry: 32 bytes at 50
        tiff += u32(0, big) + [UInt8](repeating: 0, count: 32)
        let segments = try JPEGMetadata.segments(of: Data(main)).segments
        let at = 2 + segments.prefix(count).reduce(0) { $0 + $1.raw.count }
        var bytes = Array(main[..<at]) + segment(0xE2, MultiPictureIndex.signature + tiff) + Array(main[at...])
        let header = at + 8, entries = header + 50
        func put(_ offset: Int, _ value: Int) { bytes.replaceSubrange(offset..<(offset + 4), with: u32(value, big)) }
        put(entries, 0x030000)  // the main picture: baseline, representative
        put(entries + 4, bytes.count)
        put(entries + 8, 0)
        put(entries + 16 + 4, second.count)
        put(entries + 16 + 8, bytes.count - header)
        return bytes + second
    }

    /// The main picture's size and where the second starts in the file, from a file's MP Entries, read here
    /// independently of MultiPictureIndex.
    private func index(_ bytes: [UInt8]) throws -> (size: Int, second: Int) {
        let header = try XCTUnwrap(offset(of: 0xE2, in: bytes)) + 8
        let big = bytes[header] == 0x4D
        func u32(_ at: Int) -> Int {
            let b = bytes[at..<(at + 4)].map(Int.init)
            return big ? b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3] : b[3] << 24 | b[2] << 16 | b[1] << 8 | b[0]
        }
        return (u32(header + 50 + 4), header + u32(header + 50 + 16 + 8))
    }

    /// A gain map: a small JPEG of its own.
    private func gainMap() throws -> [UInt8] { try encoded(width: 8, height: 4) }

    func testAnUltraHDRPhotoIsCaptionedWithItsIndexStillPointingAtBothPictures() throws {
        // Apple's layout: JFIF, EXIF, then the index, and no XMP, so the new XMP goes in before the index, which moves
        // with it; only the main picture's size changes.
        for big in [false, true] {
            let second = try gainMap()
            let url = try makeJPEG("hdr-\(big).jpg", xmp: .none)
            let before = try multiPicture([UInt8](Data(contentsOf: url)), second: second, big: big, after: 2)
            try Data(before).write(to: url)
            XCTAssertEqual(try index(before).size, before.count - second.count)

            let read = try FileMetadataWriter.write(.init(caption: short, accessibilityDescription: short), to: url)
            XCTAssertEqual(read.caption, short)
            XCTAssertEqual(read.altText, short)
            let after = [UInt8](try Data(contentsOf: url))
            XCTAssertGreaterThan(after.count, before.count)
            let (size, start) = try index(after)
            XCTAssertEqual(size, after.count - second.count, "big-endian: \(big)")
            XCTAssertEqual(Array(after[(size - 2)..<size]), [0xFF, 0xD9])
            XCTAssertEqual(Array(after[start...]), second, "the gain map is byte for byte where the index says")
            // The order kept: EXIF, the new XMP, then the index.
            let markers = try segments(url).map { JPEGMetadata.isXMP($0) ? "XMP" : JPEGMetadata.isEXIF($0) ? "EXIF"
                : MultiPictureIndex.isIndex($0) ? "MPF" : String(format: "%02X", $0.marker) }
            XCTAssertEqual(Array(markers.prefix(4)), ["E0", "EXIF", "XMP", "MPF"])
            // Only the size field in the index changed.
            let old = try XCTUnwrap(JPEGMetadata.segments(of: Data(before)).segments.first(where: MultiPictureIndex.isIndex))
            let new = try XCTUnwrap(segments(url).first(where: MultiPictureIndex.isIndex))
            XCTAssertEqual(old.raw.count, new.raw.count)
            XCTAssertLessThanOrEqual(zip(old.raw, new.raw).filter { $0 != $1 }.count, 4)
        }
    }

    func testAnIndexBeforeTheXMPHasTheSecondPicturesStartMovedToo() throws {
        // The index right after the start of the file, ahead of an XMP that grows: the index stays put, so the second
        // picture moves away from it.
        for big in [false, true] {
            let second = try gainMap()
            let main = [UInt8](try Data(contentsOf: makeJPEG("main-\(big).jpg")))
            let jpeg = try multiPicture(main, second: second, big: big, after: 0)
            let before = try index(jpeg)

            let changed = [UInt8](try JPEGMetadata.rewrite(Data(jpeg), setting: [FileMetadataWriter.captionPath: short]))
            let delta = changed.count - jpeg.count
            XCTAssertGreaterThan(delta, 0)
            let after = try index(changed)
            XCTAssertEqual(after.size, before.size + delta)
            XCTAssertEqual(after.second, before.second + delta)
            XCTAssertEqual(Array(changed[after.second...]), second)
        }
    }

    func testAnXMPThatShrinksAfterTheIndexMovesTheSecondPictureBack() throws {
        // An XMP with a lot of padding, after the index, is written smaller: everything moves the other way.
        let padded = exifToolXMP().replacingOccurrences(of: "<?xpacket end", with: String(repeating: " ", count: 8000) + "<?xpacket end")
        let main = [UInt8](try Data(contentsOf: makeJPEG(xmp: .some(padded))))
        let second = try gainMap()
        let jpeg = try multiPicture(main, second: second, big: true, after: 0)
        let before = try index(jpeg)
        let changed = [UInt8](try JPEGMetadata.rewrite(Data(jpeg), setting: [FileMetadataWriter.captionPath: short]))
        let delta = changed.count - jpeg.count
        XCTAssertLessThan(delta, 0)
        let after = try index(changed)
        XCTAssertEqual(after.size, before.size + delta)
        XCTAssertEqual(Array(changed[after.second...]), second)
    }

    func testAMainSizeRecordedLongOrShortIsKeptInStep() throws {
        // libultrahdr's Apple sample records the main picture 70 bytes long, into the gain map; Pixel's photos record
        // it about 300 bytes short. Where the gain map starts is exact, which is what readers use, so these are captioned
        // and the size is kept in step.
        for (big, off) in [(true, 70), (false, -256)] {
            let second = try gainMap()
            let main = [UInt8](try Data(contentsOf: makeJPEG("off-\(off).jpg", xmp: .none)))
            var jpeg = try multiPicture(main, second: second, big: big, after: 2)
            let sizeAt = try XCTUnwrap(offset(of: 0xE2, in: jpeg)) + 8 + 50 + 4
            jpeg.replaceSubrange(sizeAt..<(sizeAt + 4), with: u32(try index(jpeg).size + off, big))
            let before = try index(jpeg)

            let changed = [UInt8](try JPEGMetadata.rewrite(Data(jpeg), setting: [FileMetadataWriter.captionPath: short]))
            let after = try index(changed)
            XCTAssertEqual(after.size, before.size + changed.count - jpeg.count)
            XCTAssertEqual(Array(changed[after.second...]), second)
        }
    }

    func testAnIndexThatCantBeFollowedIsRefusedWhenTheFileWouldGrow() throws {
        // Offsets within the MPF segment's body ("MPF\0" at 0, the header at 4), little-endian, as multiPicture writes
        // it; the same cases as the Windows app's tests (#150).
        let cases: [(String, Int, [UInt8])] = [
            ("type", 40, [4, 0]),
            ("count of none", 42, [0, 0, 0, 0]),
            ("count not a whole entry", 42, [33, 0, 0, 0]),
            ("count too many", 42, [0, 8, 0, 0]),
            ("entries past the segment", 46, [0, 1, 0, 0]),
            ("IFD past the segment", 8, [0, 1, 0, 0]),
            ("no MP Entry tag", 38, [0x05, 0xB0]),
            ("main picture not at the start", 62, [1, 0, 0, 0]),
            ("main size past the file", 58, [0xFF, 0xFF, 0xFF, 0x7F]),
            ("second picture's start missing", 78, [0, 0, 0, 0]),
            ("second picture's start one byte out", 78, []),
            ("not a byte order", 4, [0x58, 0x58]),
        ]
        let second = try gainMap()
        let main = [UInt8](try Data(contentsOf: makeJPEG(xmp: .none)))
        let good = try multiPicture(main, second: second, big: false, after: 2)
        XCTAssertNoThrow(try JPEGMetadata.rewrite(Data(good), setting: [FileMetadataWriter.captionPath: short]))
        let body = try XCTUnwrap(offset(of: 0xE2, in: good)) + 4
        for (what, at, value) in cases {
            var jpeg = good
            if value.isEmpty { jpeg[body + at] &+= 1 } else { jpeg.replaceSubrange((body + at)..<(body + at + value.count), with: value) }
            XCTAssertThrowsError(try JPEGMetadata.rewrite(Data(jpeg), setting: [FileMetadataWriter.captionPath: short]), what) { error in
                XCTAssertEqual(error as? FileMetadataWriteError, .multiPicture, what)
            }
        }
    }

    func testAFileWithTwoIndexesIsntGrown() throws {
        let main = [UInt8](try Data(contentsOf: makeJPEG(xmp: .none)))
        let jpeg = try multiPicture(main, second: try gainMap(), big: false, after: 2)
        let segment = try XCTUnwrap(JPEGMetadata.segments(of: Data(jpeg)).segments.first(where: MultiPictureIndex.isIndex))
        let at = try XCTUnwrap(offset(of: 0xE2, in: jpeg))
        let twice = Array(jpeg[..<at]) + segment.raw + Array(jpeg[at...])
        XCTAssertThrowsError(try JPEGMetadata.rewrite(Data(twice), setting: [FileMetadataWriter.captionPath: short])) { error in
            XCTAssertEqual(error as? FileMetadataWriteError, .multiPicture)
        }
    }

    func testAnIndexThatCantBeFollowedIsStillChangedWhenTheSizeStaysTheSame() throws {
        // Written once, the XMP has the same size when the same caption is written again, so the index needn't change.
        let url = try makeJPEG()
        try FileMetadataWriter.write(.init(caption: short), to: url)
        var bytes = [UInt8](try Data(contentsOf: url))
        let body: [UInt8] = Array("MPF".utf8) + [0] + [0x4D, 0x4D, 0, 42, 0, 0, 0, 8, 0, 0]
        bytes.insert(contentsOf: segment(0xE2, body), at: 2)
        let changed = try JPEGMetadata.rewrite(Data(bytes), setting: [FileMetadataWriter.captionPath: short])
        XCTAssertEqual(changed.count, bytes.count)
    }

    func testAnHDRPhotoImageIOWroteKeepsAGainMapImageIOStillFinds() throws {
        // A JPEG with a gain map as the Mac writes one (its HDR photos' layout), captioned, then read back by ImageIO.
        let url = temp.appendingPathComponent("apple-hdr.jpg")
        let context = try XCTUnwrap(CGContext(data: nil, width: 64, height: 32, bitsPerComponent: 8, bytesPerRow: 0,
                                              space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(red: 0.2, green: 0.5, blue: 0.8, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: 64, height: 32))
        let gain = (0..<(32 * 16)).map { UInt8($0 % 256) }
        let description: [CFString: Any] = [kCGImagePropertyWidth: 32, kCGImagePropertyHeight: 16, kCGImagePropertyBytesPerRow: 32,
                                            kCGImagePropertyPixelFormat: kCVPixelFormatType_OneComponent8]
        let metadata = CGImageMetadataCreateMutable()
        _ = CGImageMetadataRegisterNamespaceForPrefix(metadata, "http://ns.apple.com/HDRGainMap/1.0/" as CFString,
                                                      "HDRGainMap" as CFString, nil)
        _ = CGImageMetadataSetValueWithPath(metadata, nil, "HDRGainMap:HDRGainMapVersion" as CFString, 65536 as CFNumber)
        let info: [CFString: Any] = [kCGImageAuxiliaryDataInfoData: Data(gain),
                                     kCGImageAuxiliaryDataInfoDataDescription: description,
                                     kCGImageAuxiliaryDataInfoMetadata: metadata]
        let destination = try XCTUnwrap(CGImageDestinationCreateWithURL(url as CFURL, UTType.jpeg.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, try XCTUnwrap(context.makeImage()), nil)
        CGImageDestinationAddAuxiliaryDataInfo(destination, kCGImageAuxiliaryDataTypeHDRGainMap, info as CFDictionary)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        func gainMap() -> Data? {
            CGImageSourceCreateWithURL(url as CFURL, nil)
                .flatMap { CGImageSourceCopyAuxiliaryDataInfoAtIndex($0, 0, kCGImageAuxiliaryDataTypeHDRGainMap) as? [CFString: Any] }
                .flatMap { $0[kCGImageAuxiliaryDataInfoData] as? Data }
        }
        let before = try Data(contentsOf: url)
        guard JPEGLayout.hasMultiPicture(before), let map = gainMap() else {
            throw XCTSkip("This macOS didn't write the gain map with a Multi-Picture index.")
        }

        let read = try FileMetadataWriter.write(.init(caption: short, accessibilityDescription: short), to: url)
        XCTAssertEqual(read.caption, short)
        XCTAssertGreaterThan(try Data(contentsOf: url).count, before.count)
        XCTAssertEqual(gainMap(), map, "ImageIO still finds the same gain map through the patched index")
        XCTAssertEqual(JPEGLayout.trailingData(try Data(contentsOf: url)), JPEGLayout.trailingData(before))

        // The index's sizes and starts, before and after: the main picture grew by what the file did, and the gain map
        // starts where it did relative to the picture's end.
        func layout(_ bytes: Data) throws -> (index: MultiPictureIndex, end: Int) {
            let list = try JPEGMetadata.segments(of: bytes).segments
            let position = list.firstIndex(where: MultiPictureIndex.isIndex)!
            let start = 2 + list[..<position].reduce(0) { $0 + $1.raw.count }
            let header = start + list[position].bodyStart + MultiPictureIndex.signature.count
            let index = try MultiPictureIndex.read([UInt8](bytes), header: header, segmentEnd: start + list[position].raw.count)
            return (index, try XCTUnwrap(JPEGLayout.endOfPicture(bytes)))
        }
        let after = try Data(contentsOf: url)
        let (old, oldEnd) = try layout(before), (new, newEnd) = try layout(after)
        XCTAssertEqual(new.sizes[0] - old.sizes[0], after.count - before.count)
        XCTAssertEqual(new.header + new.offsets[1] - newEnd, old.header + old.offsets[1] - oldEnd)
    }

    func testAnIndexThatDoesntPointAtTheSecondPictureIsLeftAlone() throws {
        let url = try makeJPEG(xmp: .none)
        var jpeg = try multiPicture([UInt8](Data(contentsOf: url)), second: try gainMap(), big: false, after: 2)
        // The second picture's start, one byte out.
        jpeg[try XCTUnwrap(offset(of: 0xE2, in: jpeg)) + 8 + 50 + 16 + 8] &+= 1
        try Data(jpeg).write(to: url)
        try assertRefused(.init(caption: short), url)
        XCTAssertEqual(FileMetadataWriteError.multiPicture.errorDescription,
                       "It holds a second picture, such as an HDR map, laid out in a way that can't be updated safely.")
    }
}
