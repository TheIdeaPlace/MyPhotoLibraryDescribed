import ImageIO
import XCTest
import zlib
@testable import Photo_Finder

/// Writing into PNG files by editing their chunks (#61). The PNGs are built chunk by chunk, like a macOS screenshot
/// (an eXIf chunk with an ImageDescription, a UserComment and an Orientation) with XMP as ExifTool writes it.
final class PNGMetadataTests: XCTestCase {
    private var temp: URL!

    override func setUpWithError() throws {
        temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: temp)
    }

    // MARK: - Building PNGs

    private static let width = 16, height = 8
    private static let userComment: [UInt8] = Array("ASCII".utf8) + [0, 0, 0] + Array("Screenshot".utf8)
    private static let dateTaken = "2024:06:01 10:11:12"
    private static let trailing: [UInt8] = Array("after the end".utf8)

    private func crc(_ bytes: [UInt8]) -> UInt32 {
        bytes.withUnsafeBufferPointer { UInt32(crc32(0, $0.baseAddress, uInt($0.count))) }
    }

    private func u16(_ v: UInt16, _ big: Bool) -> [UInt8] {
        big ? [UInt8(v >> 8), UInt8(v & 0xFF)] : [UInt8(v & 0xFF), UInt8(v >> 8)]
    }

    private func u32(_ v: UInt32, _ big: Bool) -> [UInt8] {
        let b = [UInt8(v >> 24), UInt8((v >> 16) & 0xFF), UInt8((v >> 8) & 0xFF), UInt8(v & 0xFF)]
        return big ? b : b.reversed()
    }

    private func chunk(_ type: String, _ body: [UInt8]) -> [UInt8] {
        let name = Array(type.utf8)
        return u32(UInt32(body.count), true) + name + body + u32(crc(name + body), true)
    }

    private func deflate(_ bytes: [UInt8]) -> [UInt8] {
        var length = compressBound(uLong(bytes.count))
        var out = [UInt8](repeating: 0, count: Int(length))
        let status = out.withUnsafeMutableBufferPointer { o in
            bytes.withUnsafeBufferPointer { compress(o.baseAddress, &length, $0.baseAddress, uLong(bytes.count)) }
        }
        XCTAssertEqual(status, Z_OK)
        return Array(out[0..<Int(length)])
    }

    private func entry(_ tag: UInt16, _ type: UInt16, _ count: UInt32, _ value: [UInt8], _ big: Bool) -> [UInt8] {
        u16(tag, big) + u16(type, big) + u32(count, big) + value + [UInt8](repeating: 0, count: 4 - value.count)
    }

    /// TIFF data like a screenshot's: IFD0 with the description (when given), Orientation 3 and the EXIF pointer;
    /// then the EXIF directory with DateTimeOriginal, UserComment and the picture's size.
    private func exif(big: Bool, description: String? = "Screenshot") -> [UInt8] {
        let ifd0Count = description == nil ? 2 : 3
        let ifd0End = 8 + 2 + ifd0Count * 12 + 4
        var text: [UInt8] = description.map { Array($0.utf8) + [0] } ?? []
        if text.count % 2 == 1 { text.append(0) }
        let exifIFD = ifd0End + text.count
        let exifEnd = exifIFD + 2 + 4 * 12 + 4
        let date = Array(Self.dateTaken.utf8) + [0]

        var bytes = (big ? Array("MM".utf8) : Array("II".utf8)) + u16(42, big) + u32(8, big)
        bytes += u16(UInt16(ifd0Count), big)
        if let description {
            bytes += entry(0x010E, 2, UInt32(description.utf8.count + 1), u32(UInt32(ifd0End), big), big)
        }
        bytes += entry(0x0112, 3, 1, u16(3, big), big)
        bytes += entry(0x8769, 4, 1, u32(UInt32(exifIFD), big), big)
        bytes += u32(0, big)
        bytes += text
        XCTAssertEqual(bytes.count, exifIFD)
        bytes += u16(4, big)
        bytes += entry(0x9003, 2, UInt32(date.count), u32(UInt32(exifEnd), big), big)
        bytes += entry(0x9286, 7, UInt32(Self.userComment.count), u32(UInt32(exifEnd + date.count), big), big)
        bytes += entry(0xA002, 4, 1, u32(UInt32(Self.width), big), big)
        bytes += entry(0xA003, 4, 1, u32(UInt32(Self.height), big), big)
        bytes += u32(0, big)
        bytes += date + Self.userComment
        return bytes
    }

    /// XMP as ExifTool writes it, with keywords, a creator tool and a rating alongside the caption.
    private func exifToolXMP(caption: String? = "Screenshot", title: String? = nil) -> String {
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

    private func xmpChunk(_ packet: String, compressed: Bool) -> [UInt8] {
        let keyword = Array("XML:com.adobe.xmp".utf8) + [0]
        let text = Array(packet.utf8)
        return chunk("iTXt", keyword + (compressed ? [1, 0, 0, 0] + deflate(text) : [0, 0, 0, 0] + text))
    }

    /// The picture: 16 by 8 RGB, each row with filter 0, split over two IDAT chunks.
    private func idats() -> [[UInt8]] {
        var raw: [UInt8] = []
        for y in 0..<Self.height {
            raw.append(0)
            for x in 0..<Self.width { raw += [UInt8(x * 16), UInt8(y * 32), 128] }
        }
        let data = deflate(raw)
        let half = data.count / 2
        return [chunk("IDAT", Array(data[0..<half])), chunk("IDAT", Array(data[half...]))]
    }

    enum XMPPlace { case none, beforePicture, afterPicture }

    @discardableResult
    private func makePNG(_ name: String = "Screenshot.png", big: Bool = true, description: String? = "Screenshot",
                         withEXIF: Bool = true, xmp: String? = nil, place: XMPPlace = .beforePicture,
                         compressed: Bool = false) throws -> URL {
        let url = temp.appendingPathComponent(name)
        var ihdr = u32(UInt32(Self.width), true) + u32(UInt32(Self.height), true)
        ihdr += [8, 2, 0, 0, 0]
        var bytes = PNGMetadata.signature + chunk("IHDR", ihdr) + chunk("sRGB", [0])
        if withEXIF { bytes += chunk("eXIf", exif(big: big, description: description)) }
        let packet = xmp ?? exifToolXMP()
        if place == .beforePicture { bytes += xmpChunk(packet, compressed: compressed) }
        bytes += chunk("tEXt", Array("Software".utf8) + [0] + Array("Test maker".utf8))
        for idat in idats() { bytes += idat }
        if place == .afterPicture { bytes += xmpChunk(packet, compressed: compressed) }
        bytes += chunk("IEND", []) + Self.trailing
        try Data(bytes).write(to: url)
        let old = Date(timeIntervalSince1970: 1_700_000_000.123456)
        try FileManager.default.setAttributes([.modificationDate: old, .creationDate: old.addingTimeInterval(-60)],
                                              ofItemAtPath: url.path)
        return url
    }

    // MARK: - Reading what was written

    private func chunks(_ url: URL) throws -> [PNGMetadata.Chunk] {
        try PNGMetadata.chunks(of: Data(contentsOf: url)).chunks
    }

    /// Every chunk but the XMP and the EXIF, which are the ones allowed to change.
    private func untouchedChunks(_ url: URL) throws -> [PNGMetadata.Chunk] {
        try chunks(url).filter { $0.type != "eXIf" && !($0.type == "iTXt" && PNGMetadata.isXMP($0)) }
    }

    private func exifBody(_ url: URL) throws -> [UInt8] {
        Array(try XCTUnwrap(chunks(url).first { $0.type == "eXIf" }).body)
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

    private func modified(_ url: URL) -> timespec {
        var info = stat()
        stat(url.path, &info)
        return info.st_mtimespec
    }

    private func userComment(_ url: URL) throws -> String? {
        let exif = try properties(url)[kCGImagePropertyExifDictionary] as? [CFString: Any]
        return exif?[kCGImagePropertyExifUserComment] as? String
    }

    private func tiffDescription(_ url: URL) throws -> String? {
        let tiff = try properties(url)[kCGImagePropertyTIFFDictionary] as? [CFString: Any]
        return tiff?[kCGImagePropertyTIFFImageDescription] as? String
    }

    private let short = "A brown dog runs on a sandy beach."

    // MARK: - A screenshot

    func testAScreenshotGetsItsCaptionAndAltTextAndKeepsEverythingElse() throws {
        for big in [true, false] {
            let label = big ? "big-endian" : "little-endian"
            let url = try makePNG("shot-\(label).png", big: big)
            let before = FileMetadata.read(url)
            XCTAssertEqual(before.caption, "Screenshot", label)
            XCTAssertEqual(try userComment(url), "Screenshot", label)
            let untouched = try untouchedChunks(url)
            let exifBefore = try exifBody(url)
            let pixelsBefore = try XCTUnwrap(pixels(url))
            let date = modified(url)
            let created = try url.resourceValues(forKeys: [.creationDateKey]).creationDate

            let after = try FileMetadataWriter.write(.init(caption: short, accessibilityDescription: short), to: url)

            XCTAssertEqual(after.caption, short, label)
            XCTAssertEqual(after.altText, short, label)
            XCTAssertEqual(after.extendedDescription, "", label)
            XCTAssertEqual(after.keywords, ["beach", "dog"], label)
            XCTAssertEqual(after.rating, 3, label)
            XCTAssertEqual(after.dateTaken, before.dateTaken, label)
            XCTAssertNotNil(after.dateTaken, label)
            XCTAssertEqual(xmpValue(url, "xmp:CreatorTool"), "ExifTool test", label)
            // The EXIF: its description gone, its UserComment and Orientation exactly as they were.
            XCTAssertEqual(try userComment(url), "Screenshot", label)
            XCTAssertEqual(try properties(url)[kCGImagePropertyOrientation] as? Int, 3, label)
            // ImageIO shows the XMP caption as the TIFF description once the EXIF one is gone.
            XCTAssertEqual(try tiffDescription(url), short, label)
            let exifAfter = try exifBody(url)
            XCTAssertEqual(exifAfter.count, exifBefore.count, label)
            // IFD0: two entries now (Orientation, then the EXIF pointer moved up), then the next directory's offset.
            XCTAssertEqual(Array(exifAfter[0..<8]), Array(exifBefore[0..<8]), label)
            XCTAssertEqual(Array(exifAfter[8..<10]), u16(2, big), label)
            XCTAssertEqual(Array(exifAfter[10..<(10 + 2 * 12 + 4)]), Array(exifBefore[(10 + 12)..<(10 + 3 * 12 + 4)]), label)
            XCTAssertEqual(Array(exifAfter[(10 + 2 * 12 + 4)..<(10 + 3 * 12 + 4)]), [UInt8](repeating: 0, count: 12), label)
            let exifDirectory = 8 + 2 + 3 * 12 + 4 + 12  // after IFD0 and its description
            XCTAssertEqual(Array(exifAfter[exifDirectory...]), Array(exifBefore[exifDirectory...]), "\(label): the EXIF directory moved")
            XCTAssertEqual(Array(exifAfter[(exifDirectory - 12)..<exifDirectory]), [UInt8](repeating: 0, count: 12),
                           "\(label): the old description is cleared")
            // Every other chunk byte for byte, the picture included, and what follows the end.
            XCTAssertEqual(try untouchedChunks(url), untouched, label)
            XCTAssertTrue(try Data(contentsOf: url).suffix(Self.trailing.count).elementsEqual(Self.trailing), label)
            XCTAssertEqual(pixels(url), pixelsBefore, "\(label): the picture changed")
            // The dates as they were.
            let now = modified(url)
            XCTAssertEqual(now.tv_sec, date.tv_sec, label)
            XCTAssertEqual(now.tv_nsec, date.tv_nsec, label)
            XCTAssertEqual(try URL(fileURLWithPath: url.path).resourceValues(forKeys: [.creationDateKey]).creationDate, created, label)
            // One XMP chunk, uncompressed, before the picture.
            let all = try chunks(url)
            let xmps = all.indices.filter { all[$0].type == "iTXt" && PNGMetadata.isXMP(all[$0]) }
            XCTAssertEqual(xmps.count, 1, label)
            XCTAssertLessThan(try XCTUnwrap(xmps.first), try XCTUnwrap(all.firstIndex { $0.type == "IDAT" }), label)
        }
    }

    func testOnlyTheMetadataChunksDifferAndNoTemporaryFileIsLeft() throws {
        let url = try makePNG()
        let before = try chunks(url)
        try FileMetadataWriter.write(.init(caption: short), to: url)
        let after = try chunks(url)
        XCTAssertEqual(after.map(\.type), before.map(\.type))
        for (a, b) in zip(before, after) where a.type != "eXIf" && a.type != "iTXt" {
            XCTAssertEqual(a.raw, b.raw, a.type)
        }
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: temp.path), ["Screenshot.png"])
    }

    func testAltTextAloneLeavesTheEXIFAndCaptionAsTheyWere() throws {
        let url = try makePNG()
        let exifBefore = try exifBody(url)
        let read = try FileMetadataWriter.write(.init(accessibilityDescription: short), to: url)
        XCTAssertEqual(read.altText, short)
        XCTAssertEqual(read.caption, "Screenshot")
        XCTAssertEqual(try exifBody(url), exifBefore)
    }

    func testASecondWriteReplacesTheFirstAndAnEmptyCaptionRemovesIt() throws {
        let url = try makePNG()
        var read = try FileMetadataWriter.write(.init(caption: short, accessibilityDescription: short), to: url)
        XCTAssertEqual(read.caption, short)
        read = try FileMetadataWriter.write(.init(caption: "Two dogs", accessibilityDescription: "Two dogs"), to: url)
        XCTAssertEqual(read.caption, "Two dogs")
        XCTAssertEqual(read.altText, "Two dogs")
        XCTAssertEqual(try userComment(url), "Screenshot")
        read = try FileMetadataWriter.write(.init(caption: ""), to: url)
        XCTAssertEqual(read.caption, "")
        XCTAssertNil(xmpValue(url, "dc:description"))
        XCTAssertEqual(read.altText, "Two dogs", "the alt text is left alone")
        XCTAssertEqual(read.keywords, ["beach", "dog"])
    }

    func testRemovingACaptionThatsOnlyInTheEXIF() throws {
        let url = try makePNG(xmp: exifToolXMP(caption: nil))
        XCTAssertEqual(FileMetadata.read(url).caption, "Screenshot")
        let read = try FileMetadataWriter.write(.init(caption: ""), to: url)
        XCTAssertEqual(read.caption, "")
        XCTAssertNil(try tiffDescription(url))
        XCTAssertEqual(try userComment(url), "Screenshot")
    }

    func testALongDescriptionGoesInBothFieldsAndAShortOneOrNoneClearsThem() throws {
        let url = try makePNG()
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
        let titled = try makePNG("titled.png", xmp: exifToolXMP(title: "Holiday"))
        try FileMetadataWriter.write(.init(caption: "Two dogs"), to: titled)
        XCTAssertEqual(xmpValue(titled, "dc:title"), "Holiday")
        let untitled = try makePNG("untitled.png")
        try FileMetadataWriter.write(.init(caption: "Two dogs"), to: untitled)
        XCTAssertEqual(xmpValue(untitled, "dc:title"), "Two dogs")
        let windows = try makePNG("windows.png", xmp: exifToolXMP(caption: "Rex", title: "Rex"))
        try FileMetadataWriter.write(.init(caption: "Rex on the beach"), to: windows)
        XCTAssertEqual(xmpValue(windows, "dc:title"), "Rex on the beach")
    }

    // MARK: - Where the XMP is

    func testCompressedXMPIsReadAndWrittenUncompressed() throws {
        let url = try makePNG(compressed: true)
        XCTAssertEqual(FileMetadata.read(url).keywords, ["beach", "dog"])
        let read = try FileMetadataWriter.write(.init(caption: short), to: url)
        XCTAssertEqual(read.caption, short)
        XCTAssertEqual(read.keywords, ["beach", "dog"])
        let xmp = try XCTUnwrap(chunks(url).first { $0.type == "iTXt" && PNGMetadata.isXMP($0) })
        XCTAssertEqual(xmp.body[xmp.body.startIndex + 18], 0, "written uncompressed")
    }

    func testXMPAfterThePictureMovesBeforeIt() throws {
        let url = try makePNG(place: .afterPicture)
        let untouched = try untouchedChunks(url)
        let read = try FileMetadataWriter.write(.init(caption: short, accessibilityDescription: short), to: url)
        XCTAssertEqual(read.caption, short)
        XCTAssertEqual(read.keywords, ["beach", "dog"])
        XCTAssertEqual(xmpValue(url, "xmp:CreatorTool"), "ExifTool test")
        let all = try chunks(url)
        let xmps = all.indices.filter { all[$0].type == "iTXt" && PNGMetadata.isXMP(all[$0]) }
        XCTAssertEqual(xmps, [try XCTUnwrap(all.firstIndex { $0.type == "IDAT" }) - 1])
        XCTAssertEqual(try untouchedChunks(url), untouched)
    }

    func testAPNGWithNoMetadataGetsXMPBeforeThePicture() throws {
        let url = try makePNG(withEXIF: false, place: .none)
        let untouched = try untouchedChunks(url)
        let read = try FileMetadataWriter.write(.init(caption: short, accessibilityDescription: short), to: url)
        XCTAssertEqual(read.caption, short)
        XCTAssertEqual(read.altText, short)
        XCTAssertEqual(try untouchedChunks(url), untouched)
    }

    func testASecondXMPChunkIsDroppedAndOnlyTheFirstIsUsed() throws {
        let url = try makePNG(withEXIF: false)
        var bytes = [UInt8](try Data(contentsOf: url))
        let iend = try XCTUnwrap(Data(bytes).range(of: Data("IEND".utf8))).lowerBound - 4
        bytes.insert(contentsOf: xmpChunk(exifToolXMP(caption: "Stale"), compressed: false), at: iend)
        try Data(bytes).write(to: url)
        let read = try FileMetadataWriter.write(.init(accessibilityDescription: short), to: url)
        XCTAssertEqual(read.altText, short)
        XCTAssertEqual(read.caption, "Screenshot")
        XCTAssertEqual(try chunks(url).filter { $0.type == "iTXt" && PNGMetadata.isXMP($0) }.count, 1)
    }

    func testDoubleQuotedXMPIsReadToo() throws {
        let url = try makePNG(xmp: exifToolXMP().replacingOccurrences(of: "'", with: "\""))
        let read = try FileMetadataWriter.write(.init(caption: short), to: url)
        XCTAssertEqual(read.caption, short)
        XCTAssertEqual(read.keywords, ["beach", "dog"])
    }

    // MARK: - Refusing

    func testUnreadableXMPIsRefusedAndLeftAlone() throws {
        let url = try makePNG(xmp: "<x:xmpmeta this is not XMP")
        let before = try Data(contentsOf: url)
        XCTAssertThrowsError(try FileMetadataWriter.write(.init(caption: short), to: url))
        XCTAssertEqual(try Data(contentsOf: url), before)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: temp.path), ["Screenshot.png"])
    }

    func testADamagedPNGIsRefusedAndLeftAlone() throws {
        let url = try makePNG()
        var bytes = [UInt8](try Data(contentsOf: url))
        let idat = try XCTUnwrap(Data(bytes).range(of: Data("IDAT".utf8)))
        bytes[idat.upperBound + 2] ^= 0xFF  // one byte of the picture, so its CRC no longer matches
        try Data(bytes).write(to: url)
        XCTAssertThrowsError(try FileMetadataWriter.write(.init(caption: short), to: url))
        XCTAssertEqual([UInt8](try Data(contentsOf: url)), bytes)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: temp.path), ["Screenshot.png"])
    }

    func testMalformedChunksAreRefused() throws {
        let good = [UInt8](try Data(contentsOf: makePNG()))
        // Cut short, no IEND, not a PNG at all, a bad length.
        let iend = try XCTUnwrap(Data(good).range(of: Data("IEND".utf8))).lowerBound - 4
        for bad in [Array(good[0..<(good.count / 2)]), Array(good[0..<iend]), Array("GIF89a....".utf8),
                    Array(good[0..<8]) + [0xFF, 0xFF, 0xFF, 0xFF] + Array(good[12...])] {
            XCTAssertThrowsError(try PNGMetadata.rewrite(Data(bad), setting: [FileMetadataWriter.captionPath: "x"],
                                                         removingEXIFDescription: true))
        }
    }

    func testDamagedEXIFIsRefused() {
        let shortEXIF: [UInt8] = Array("MM".utf8) + [0, 42, 0, 0, 0, 8, 0, 9]  // nine entries promised, none there
        XCTAssertThrowsError(try PNGMetadata.removingImageDescription(fromEXIF: shortEXIF))
        XCTAssertThrowsError(try PNGMetadata.removingImageDescription(fromEXIF: Array("XX".utf8) + [0, 42, 0, 0, 0, 8]))
        XCTAssertNil(try PNGMetadata.removingImageDescription(fromEXIF: exif(big: true, description: nil)))
    }
}
