import Foundation
import ImageIO

/// Changes a JPEG's caption and accessibility fields by editing its segments (#81), never through ImageIO's copy,
/// which can drop a JPEG's comment segment and most of its EXIF, the embedded thumbnail included.
///
/// Only the XMP segment (APP1 "http://ns.adobe.com/xap/1.0/") is replaced, where it was or, when there was none, after
/// the JFIF and EXIF segments at the start. It's written with about 2 KB of padding, as the Windows app writes
/// it (#89), so other tools can change it in place. When the caption changes, the EXIF description (IFD0's
/// ImageDescription) is taken out within its segment, which keeps its size and every offset in it, because ImageIO
/// reads it as the caption ahead of the XMP. Every other segment (the rest of the EXIF with its thumbnail and maker
/// note, comments, ICC profiles, Photoshop's IPTC) and every byte from the start of the picture data on (the picture,
/// its end, and anything after it, like a motion photo's video) is copied as it was.
///
/// Photoshop's IPTC segment is left as it is. While its digest matches (or there's none), ImageIO and other readers
/// following the Metadata Working Group's guidance take the XMP's caption ahead of the IPTC's, so the new caption is
/// the one read; when the digest doesn't match, the IPTC wins, the caption doesn't read back, and the write is refused.
///
/// A file with a Multi-Picture index (Ultra HDR, Apple's HDR photos, MPO) has the index patched to match (#90): see
/// `MultiPictureIndex`.
///
/// Refused, leaving the file alone: anything that doesn't parse, XMP spread over several segments (Extended XMP) or in
/// more than one segment, XMP that isn't valid UTF-8 or XML, XMP too large for one segment, and a change of size to a
/// file with two Multi-Picture indexes or one that can't be followed.
enum JPEGMetadata {
    typealias Failure = PNGMetadata.Failure

    static let xmpHeader = Array("http://ns.adobe.com/xap/1.0/".utf8) + [0]
    static let extendedXMPHeader = Array("http://ns.adobe.com/xmp/extension/".utf8) + [0]
    static let exifHeader = Array("Exif".utf8) + [0, 0]
    static let photoshopHeader = Array("Photoshop 3.0".utf8) + [0]
    /// The most a segment's body can hold: its two length bytes count themselves.
    static let maxBody = 0xFFFF - 2
    /// Room left in the XMP for the next change, as XMP writers leave it.
    static let padding = 2048

    /// A segment as it is in the file: any fill bytes, the marker, the length and the body.
    struct Segment: Equatable {
        let marker: UInt8
        let raw: [UInt8]
        /// Where the body starts in `raw`.
        let bodyStart: Int

        var body: ArraySlice<UInt8> { raw[bodyStart...] }

        init(marker: UInt8, raw: [UInt8], bodyStart: Int) {
            self.marker = marker
            self.raw = raw
            self.bodyStart = bodyStart
        }

        /// A new segment, with its length worked out. The body must fit (`maxBody`).
        init(marker: UInt8, body: [UInt8]) {
            let length = body.count + 2
            self.init(marker: marker, raw: [0xFF, marker, UInt8(length >> 8), UInt8(length & 0xFF)] + body, bodyStart: 4)
        }

        func starts(with header: [UInt8]) -> Bool { body.starts(with: header) }
    }

    // MARK: - Reading the segments

    /// The segments between the start-of-image marker and the first start-of-scan, and everything from that
    /// start-of-scan on (the picture data, its end and whatever follows), kept as it is.
    static func segments(of data: Data) throws -> (segments: [Segment], picture: [UInt8]) {
        let bytes = [UInt8](data)
        guard bytes.count >= 4, bytes[0] == 0xFF, bytes[1] == 0xD8 else { throw Failure.malformed("Not a JPEG file.") }
        var segments: [Segment] = []
        var position = 2
        while true {
            guard position < bytes.count, bytes[position] == 0xFF else {
                throw Failure.malformed("Its segments couldn't be followed.")
            }
            // Fill bytes (extra 0xFFs) may come before a marker; they stay with the segment that follows.
            var markerAt = position + 1
            while markerAt < bytes.count, bytes[markerAt] == 0xFF { markerAt += 1 }
            guard markerAt < bytes.count else { throw Failure.malformed("The file ends before its picture.") }
            let marker = bytes[markerAt]
            if marker == 0xDA {
                guard segments.contains(where: { isFrameHeader($0.marker) }) else {
                    throw Failure.malformed("Its picture has no frame header.")
                }
                return (segments, Array(bytes[position...]))
            }
            // Markers that stand alone, or end the image, have no place before the picture.
            guard marker != 0x00, marker != 0x01, marker != 0xD8, marker != 0xD9, !(0xD0...0xD7).contains(marker) else {
                throw Failure.malformed("An unexpected marker comes before the picture.")
            }
            guard markerAt + 3 <= bytes.count else { throw Failure.malformed("The file ends before its picture.") }
            let length = Int(bytes[markerAt + 1]) << 8 | Int(bytes[markerAt + 2])
            let end = markerAt + 1 + length
            guard length >= 2, end <= bytes.count else { throw Failure.malformed("A segment runs past the end of the file.") }
            segments.append(Segment(marker: marker, raw: Array(bytes[position..<end]), bodyStart: markerAt + 3 - position))
            position = end
        }
    }

    /// Start-of-frame markers: C0 to CF, but not C4 (Huffman tables), C8 (reserved) or CC (arithmetic conditioning).
    static func isFrameHeader(_ marker: UInt8) -> Bool {
        (0xC0...0xCF).contains(marker) && marker != 0xC4 && marker != 0xC8 && marker != 0xCC
    }

    static func isXMP(_ segment: Segment) -> Bool { segment.marker == 0xE1 && segment.starts(with: xmpHeader) }
    static func isExtendedXMP(_ segment: Segment) -> Bool { segment.marker == 0xE1 && segment.starts(with: extendedXMPHeader) }
    static func isEXIF(_ segment: Segment) -> Bool { segment.marker == 0xE1 && segment.starts(with: exifHeader) }
    static func isPhotoshop(_ segment: Segment) -> Bool { segment.marker == 0xED && segment.starts(with: photoshopHeader) }

    /// Whether a Photoshop segment holds an IPTC block (image resource 0x0404), where older apps keep a caption.
    static func hasIPTC(_ segments: [Segment]) -> Bool {
        let resource = Data(Array("8BIM".utf8) + [0x04, 0x04])
        return segments.contains { isPhotoshop($0) && Data($0.body).range(of: resource) != nil }
    }

    /// The XMP in the file's own XMP segment, without what ImageIO adds from the EXIF and IPTC (which reads an EXIF
    /// description as the caption). Nil when the file has none or doesn't parse.
    static func xmp(of data: Data) -> CGImageMetadata? {
        guard let segment = try? segments(of: data).segments.first(where: isXMP) else { return nil }
        return PNGMetadata.parseXMP(Array(segment.body.dropFirst(xmpHeader.count)))
    }

    // MARK: - Rewriting

    /// The file with `values` set in its XMP (nil removes a field) and, when the caption is among them, the EXIF
    /// description taken out. Every other segment, and everything from the picture data on, is copied byte for byte.
    static func rewrite(_ data: Data, setting values: [String: String?]) throws -> Data {
        let (segments, picture) = try segments(of: data)
        if segments.contains(where: isExtendedXMP) {
            throw Failure.malformed("Its XMP is spread over several segments (Extended XMP), which isn't changed.")
        }
        let xmpIndices = segments.indices.filter { isXMP(segments[$0]) }
        guard xmpIndices.count <= 1 else { throw Failure.malformed("It has more than one XMP segment.") }

        let metadata: CGMutableImageMetadata
        if let index = xmpIndices.first {
            let packet = Array(segments[index].body.dropFirst(xmpHeader.count))
            // Rewriting XMP in some other encoding (or damaged UTF-8) could garble text another app wrote.
            guard String(bytes: packet, encoding: .utf8) != nil else { throw Failure.malformed("Its XMP isn't valid UTF-8.") }
            guard let parsed = PNGMetadata.parseXMP(packet), let copy = CGImageMetadataCreateMutableCopy(parsed) else {
                throw Failure.malformed("Its XMP couldn't be read.")
            }
            metadata = copy
        } else {
            metadata = CGImageMetadataCreateMutable()
        }
        // A caption or title removed from a file whose IPTC still has one is left as an empty text: without it ImageIO
        // (and other readers) would take the IPTC's old one instead. Readers treat an empty one as none.
        var values = values
        if hasIPTC(segments) {
            for path in [FileMetadataWriter.captionPath, FileMetadataWriter.titlePath] where values[path] == .some(nil) {
                values[path] = ""
            }
        }
        PNGMetadata.set(values, in: metadata)

        // No XMP left (a caption cleared from a file whose only XMP was that): no XMP segment.
        let tags = (CGImageMetadataCopyTags(metadata) as? [CGImageMetadataTag]) ?? []
        var xmpSegment: Segment?
        if !tags.isEmpty {
            guard let xmp = CGImageMetadataCreateXMPData(metadata, nil) as Data? else {
                throw Failure.malformed("Its XMP couldn't be written.")
            }
            xmpSegment = Segment(marker: 0xE1, body: try paddedPacket([UInt8](xmp)))
        }

        var output = segments
        if values.keys.contains(FileMetadataWriter.captionPath), let index = output.firstIndex(where: isEXIF) {
            let segment = output[index]
            let tiff = Array(segment.body.dropFirst(exifHeader.count))
            if let changed = try PNGMetadata.removingImageDescription(fromEXIF: tiff) {
                // The same size: only the directory and the old text change.
                output[index] = Segment(marker: 0xE1, raw: Array(segment.raw[..<segment.bodyStart]) + exifHeader + changed,
                                        bodyStart: segment.bodyStart)
            }
        }
        // Where the old XMP was, otherwise after the JFIF and EXIF segments the file starts with.
        if let index = xmpIndices.first {
            if let xmpSegment { output[index] = xmpSegment } else { output.remove(at: index) }
        } else if let xmpSegment {
            output.insert(xmpSegment, at: output.firstIndex { !($0.marker == 0xE0 || isEXIF($0)) } ?? output.count)
        }

        var result: [UInt8] = [0xFF, 0xD8]
        for segment in output { result.append(contentsOf: segment.raw) }
        result.append(contentsOf: picture)
        try keepMultiPictureIndex(in: &result, from: data, before: segments, after: output)
        try check(Data(result), against: segments, picture)
        return Data(result)
    }

    /// A file with a Multi-Picture index (Ultra HDR, Apple's HDR photos, MPO) whose size changed has the index patched
    /// (#90): it records the first picture's size, and where the others start counted from the index itself. A file
    /// with two indexes, or one that can't be followed, is refused unless it keeps its size.
    private static func keepMultiPictureIndex(in result: inout [UInt8], from data: Data, before: [Segment],
                                              after: [Segment]) throws {
        let indexes = before.indices.filter { MultiPictureIndex.isIndex(before[$0]) }
        guard let first = indexes.first, result.count != data.count else { return }
        guard indexes.count == 1, let moved = after.firstIndex(where: MultiPictureIndex.isIndex) else {
            throw FileMetadataWriteError.multiPicture
        }
        // Where the index's segment is in each file, and its TIFF header just after "MPF\0".
        func start(of index: Int, in list: [Segment]) -> Int { 2 + list[..<index].reduce(0) { $0 + $1.raw.count } }
        let old = start(of: first, in: before), new = start(of: moved, in: after)
        let delta = result.count - data.count
        // The change is either before the index, which moved with it, or after it, which stayed put.
        guard new == old || new == old + delta else { throw FileMetadataWriteError.multiPicture }
        let segment = before[first]
        let header = old + segment.bodyStart + MultiPictureIndex.signature.count
        let index = try MultiPictureIndex.read([UInt8](data), header: header, segmentEnd: old + segment.raw.count)
        try index.patch(&result, delta: delta, moved: new != old)
    }

    /// The XMP segment's body: its header, then the packet in XMP's wrapper with room to grow, as much as fits.
    static func paddedPacket(_ xmp: [UInt8]) throws -> [UInt8] {
        let head = Array("<?xpacket begin=\"\u{FEFF}\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n".utf8)
        let tail = Array("<?xpacket end=\"w\"?>".utf8)
        let bare = xmpHeader.count + head.count + xmp.count + 1 + tail.count
        guard bare <= maxBody else { throw Failure.malformed("Its XMP would be too large for one JPEG segment.") }
        let room = min(padding, maxBody - bare)
        // Spaces, with a line break every hundred, as XMP's padding is written.
        let spaces = (0..<room).map { $0 % 100 == 99 ? UInt8(ascii: "\n") : UInt8(ascii: " ") }
        return xmpHeader + head + xmp + [UInt8(ascii: "\n")] + spaces + tail
    }

    /// A last look before the file is written: the same segments in the same order but the XMP, the EXIF changed only
    /// by its description being taken out (#111), the Multi-Picture index the same size, everything else byte for
    /// byte, and the picture data and what follows it exactly as they were.
    static func check(_ result: Data, against before: [Segment], _ picture: [UInt8]) throws {
        let (after, newPicture) = try segments(of: result)
        func others(_ list: [Segment]) -> [Segment] { list.filter { !isXMP($0) } }
        func inPlace(_ old: Segment, _ new: Segment) -> Bool {
            guard old.raw.count == new.raw.count else { return false }
            if isEXIF(old), isEXIF(new) {
                // The fill bytes, marker, length and "Exif\0\0" as they were; in the TIFF data after them, only IFD0's
                // table and the old description's bytes.
                let head = old.bodyStart + exifHeader.count
                return new.bodyStart == old.bodyStart && old.raw[..<head] == new.raw[..<head]
                    && PNGMetadata.onlyImageDescriptionRemoved(from: Array(old.raw[head...]), to: Array(new.raw[head...]))
            }
            return MultiPictureIndex.isIndex(old) && MultiPictureIndex.isIndex(new)
        }
        let a = others(before), b = others(after)
        guard newPicture == picture, a.count == b.count, after.filter(isXMP).count <= 1,
              zip(a, b).allSatisfy({ old, new in old == new || inPlace(old, new) })
        else { throw Failure.malformed("The new copy didn't keep the rest of the file as it was.") }
    }
}
