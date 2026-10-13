import Foundation
import ImageIO
import zlib

/// Changes a PNG's caption and accessibility fields by editing its chunks (#61), never through ImageIO's copy, which
/// damages a PNG's EXIF UserComment and can't replace the EXIF description every macOS and iOS screenshot carries.
///
/// The file's XMP (the iTXt chunk "XML:com.adobe.xmp") is read, changed and written back as one uncompressed chunk.
/// When the caption changes, the EXIF description (IFD0's ImageDescription in the eXIf chunk) is removed, since ImageIO
/// reads it as the caption ahead of the XMP. Every other byte of the file is copied as it was, the picture included.
/// Anything that doesn't parse is refused, so the caller leaves the file alone.
enum PNGMetadata {
    enum Failure: Error, Equatable {
        case malformed(String)
    }

    static let signature: [UInt8] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]
    static let xmpKeyword = Array("XML:com.adobe.xmp".utf8)

    /// A chunk as it is in the file: length, type, data and CRC.
    struct Chunk: Equatable {
        let type: String
        let raw: [UInt8]

        var body: ArraySlice<UInt8> { raw[8..<(raw.count - 4)] }

        init(type: String, raw: [UInt8]) {
            self.type = type
            self.raw = raw
        }

        /// A new chunk, with its length and CRC worked out.
        init(type: String, body: [UInt8]) {
            let typeBytes = Array(type.utf8)
            var raw = PNGMetadata.bigEndian(UInt32(body.count))
            raw += typeBytes
            raw += body
            raw += PNGMetadata.bigEndian(PNGMetadata.crc(typeBytes + body))
            self.init(type: type, raw: raw)
        }
    }

    // MARK: - Reading the chunks

    /// The file's chunks up to and including IEND, and whatever follows IEND (kept as it is).
    static func chunks(of data: Data) throws -> (chunks: [Chunk], trailing: [UInt8]) {
        let bytes = [UInt8](data)
        guard bytes.count >= 8, Array(bytes[0..<8]) == signature else { throw Failure.malformed("Not a PNG file.") }
        var chunks: [Chunk] = []
        var position = 8
        while true {
            guard position + 12 <= bytes.count else { throw Failure.malformed("The file ends before its last chunk.") }
            let length = Int(readUInt32(bytes, position, bigEndian: true))
            guard length <= 0x7FFF_FFFF, position + 12 + length <= bytes.count else {
                throw Failure.malformed("A chunk runs past the end of the file.")
            }
            let typeBytes = Array(bytes[(position + 4)..<(position + 8)])
            guard typeBytes.allSatisfy({ (0x41...0x5A).contains($0) || (0x61...0x7A).contains($0) }),
                  let type = String(bytes: typeBytes, encoding: .ascii) else {
                throw Failure.malformed("A chunk has an invalid name.")
            }
            let end = position + 12 + length
            let stored = readUInt32(bytes, end - 4, bigEndian: true)
            guard stored == crc(Array(bytes[(position + 4)..<(end - 4)])) else {
                throw Failure.malformed("The \(type) chunk is damaged.")
            }
            if chunks.isEmpty, type != "IHDR" { throw Failure.malformed("The file doesn't start with its header.") }
            chunks.append(Chunk(type: type, raw: Array(bytes[position..<end])))
            position = end
            if type == "IEND" { break }
        }
        guard chunks.contains(where: { $0.type == "IDAT" }) else { throw Failure.malformed("The file has no picture data.") }
        return (chunks, Array(bytes[position...]))
    }

    // MARK: - Rewriting

    /// The file with `values` set in its XMP (nil removes a field) and, when `removingEXIFDescription`, the EXIF
    /// description taken out. Every other chunk, and anything after IEND, is copied byte for byte.
    static func rewrite(_ data: Data, setting values: [String: String?], removingEXIFDescription: Bool) throws -> Data {
        let (chunks, trailing) = try chunks(of: data)
        let xmpIndices = chunks.indices.filter { chunks[$0].type == "iTXt" && isXMP(chunks[$0]) }
        guard let firstIDAT = chunks.firstIndex(where: { $0.type == "IDAT" }) else { throw Failure.malformed("No picture data.") }

        let metadata: CGMutableImageMetadata
        if let first = xmpIndices.first {
            let packet = try xmpPacket(chunks[first])
            guard let parsed = parseXMP(packet), let copy = CGImageMetadataCreateMutableCopy(parsed) else {
                throw Failure.malformed("Its XMP couldn't be read.")
            }
            metadata = copy
        } else {
            metadata = CGImageMetadataCreateMutable()
        }
        set(values, in: metadata)
        // No XMP left (a caption cleared from a file whose only XMP was that, or nothing to add to a file without XMP):
        // no XMP chunk, rather than refusing the write (#76's review).
        let tags = (CGImageMetadataCopyTags(metadata) as? [CGImageMetadataTag]) ?? []
        let xmpChunk: Chunk?
        if tags.isEmpty {
            xmpChunk = nil
        } else {
            guard let xmp = CGImageMetadataCreateXMPData(metadata, nil) as Data? else {
                throw Failure.malformed("Its XMP couldn't be written.")
            }
            // Keyword, its terminator, uncompressed, compression method, no language, no translated keyword.
            xmpChunk = Chunk(type: "iTXt", body: xmpKeyword + [0, 0, 0, 0, 0] + [UInt8](xmp))
        }
        // Where the old XMP was when it came before the picture, otherwise just before it, where readers look first.
        let insertAt = xmpIndices.first.map { $0 < firstIDAT ? $0 : firstIDAT } ?? firstIDAT

        var output = Data(signature)
        for (index, chunk) in chunks.enumerated() {
            if index == insertAt, let xmpChunk { output.append(contentsOf: xmpChunk.raw) }
            if xmpIndices.contains(index) { continue }
            if chunk.type == "eXIf", removingEXIFDescription,
               let exif = try removingImageDescription(fromEXIF: Array(chunk.body)) {
                output.append(contentsOf: Chunk(type: "eXIf", body: exif).raw)
            } else {
                output.append(contentsOf: chunk.raw)
            }
        }
        output.append(contentsOf: trailing)
        try check(output, against: chunks, trailing)
        return output
    }

    /// A last look before the file is written (#111): the same chunks in the same order but the XMP, the EXIF changed
    /// only by its description being taken out, and whatever follows IEND exactly as it was.
    static func check(_ result: Data, against before: [Chunk], _ trailing: [UInt8]) throws {
        let (after, newTrailing) = try chunks(of: result)
        func others(_ list: [Chunk]) -> [Chunk] { list.filter { !($0.type == "iTXt" && isXMP($0)) } }
        let a = others(before), b = others(after)
        guard newTrailing == trailing, a.count == b.count, after.count - b.count <= 1,
              zip(a, b).allSatisfy({ old, new in
                  old == new || (old.type == "eXIf" && new.type == "eXIf"
                                 && onlyImageDescriptionRemoved(from: Array(old.body), to: Array(new.body)))
              })
        else { throw Failure.malformed("The new copy didn't keep the rest of the file as it was.") }
    }

    /// Sets each field through its `[x-default]` path, as FileMetadataWriter does, or removes it for nil. Other
    /// languages holding the text being replaced (the default's, or the only language's when there's no default) are
    /// the same caption, so they change too; translations that differ are kept, as on Windows (#89).
    static func set(_ values: [String: String?], in metadata: CGMutableImageMetadata) {
        _ = CGImageMetadataRegisterNamespaceForPrefix(metadata, FileMetadataWriter.iptcNamespace, "Iptc4xmpCore" as CFString, nil)
        for (path, text) in values {
            if let text {
                for language in languages(holdingDefaultOf: path, in: metadata) {
                    _ = CGImageMetadataSetValueWithPath(metadata, nil, "\(path)[\(language)]" as CFString, text as CFString)
                }
                _ = CGImageMetadataSetValueWithPath(metadata, nil, "\(path)[x-default]" as CFString, text as CFString)
            } else {
                _ = CGImageMetadataRemoveTagWithPath(metadata, nil, path as CFString)
            }
        }
    }

    /// The languages other than x-default in a language alternative holding the same text as its default, or as its
    /// only language when it has no default.
    private static func languages(holdingDefaultOf path: String, in metadata: CGImageMetadata) -> [String] {
        guard let tag = CGImageMetadataCopyTagWithPath(metadata, nil, path as CFString),
              let items = CGImageMetadataTagCopyValue(tag) as? [CGImageMetadataTag] else { return [] }
        let languages: [(language: String, text: String?)] = items.map { item in
            let qualifiers = (CGImageMetadataTagCopyQualifiers(item) as? [CGImageMetadataTag]) ?? []
            let language = qualifiers.first { CGImageMetadataTagCopyName($0) as String? == "lang" }
                .flatMap { CGImageMetadataTagCopyValue($0) as? String } ?? ""
            return (language, CGImageMetadataTagCopyValue(item) as? String)
        }
        let others = languages.filter { !$0.language.isEmpty && $0.language.lowercased() != "x-default" }
        let old = languages.first { $0.language.lowercased() == "x-default" }?.text
            ?? (languages.count == 1 ? languages[0].text : nil)
        guard let old else { return [] }
        return others.filter { $0.text == old }.map(\.language)
    }

    // MARK: - XMP

    static func isXMP(_ chunk: Chunk) -> Bool {
        let body = chunk.body
        guard body.count > xmpKeyword.count else { return false }
        let start = body.startIndex
        return Array(body[start..<(start + xmpKeyword.count)]) == xmpKeyword && body[start + xmpKeyword.count] == 0
    }

    /// The XMP in the file's own XMP chunk, without what ImageIO adds from the EXIF (which reads a screenshot's EXIF
    /// description as its caption). Nil when the file has none or doesn't parse.
    static func xmp(of data: Data) -> CGImageMetadata? {
        guard let chunks = try? chunks(of: data).chunks, let chunk = chunks.first(where: { $0.type == "iTXt" && isXMP($0) }),
              let packet = try? xmpPacket(chunk) else { return nil }
        return parseXMP(packet)
    }

    /// The XMP read by ImageIO. Its parser refuses a packet whose x:xmpmeta namespace is in single quotes, as ExifTool
    /// writes it (`xmlns:x='adobe:ns:meta/'`), so that one attribute is tried again in double quotes, which is the
    /// same XML.
    static func parseXMP(_ packet: [UInt8]) -> CGImageMetadata? {
        if let parsed = CGImageMetadataCreateFromXMPData(Data(packet) as CFData) { return parsed }
        let single = Data("xmlns:x='adobe:ns:meta/'".utf8), double = Data("xmlns:x=\"adobe:ns:meta/\"".utf8)
        var data = Data(packet)
        guard let range = data.range(of: single) else { return nil }
        data.replaceSubrange(range, with: double)
        return CGImageMetadataCreateFromXMPData(data as CFData)
    }

    /// The XMP text of an iTXt XMP chunk, inflated when it's compressed.
    static func xmpPacket(_ chunk: Chunk) throws -> [UInt8] {
        let body = Array(chunk.body)
        var position = xmpKeyword.count + 1
        guard position + 2 <= body.count else { throw Failure.malformed("Its XMP chunk is cut short.") }
        let compressed = body[position], method = body[position + 1]
        position += 2
        // The language tag and the translated keyword, each ending in a zero.
        for _ in 0..<2 {
            guard let zero = body[position...].firstIndex(of: 0) else { throw Failure.malformed("Its XMP chunk is cut short.") }
            position = zero + 1
        }
        let text = Array(body[position...])
        switch (compressed, method) {
        case (0, _): return text
        case (1, 0): return try inflate(text)
        default: throw Failure.malformed("Its XMP is compressed in an unknown way.")
        }
    }

    static func inflate(_ input: [UInt8]) throws -> [UInt8] {
        var capacity = max(input.count * 4, 64 * 1024)
        while capacity <= 256 * 1024 * 1024 {
            var output = [UInt8](repeating: 0, count: capacity)
            var length = uLongf(capacity)
            let status = output.withUnsafeMutableBufferPointer { out in
                input.withUnsafeBufferPointer { uncompress(out.baseAddress, &length, $0.baseAddress, uLong(input.count)) }
            }
            switch status {
            case Z_OK: return Array(output[0..<Int(length)])
            case Z_BUF_ERROR: capacity *= 2
            default: throw Failure.malformed("Its compressed XMP is damaged.")
            }
        }
        throw Failure.malformed("Its compressed XMP is too large.")
    }

    // MARK: - EXIF

    /// The EXIF with IFD0's ImageDescription (0x010E) taken out, or nil when it has none. The entries after it move
    /// up, the freed bytes and the old text become zeros, and everything else stays exactly where it was, so every
    /// offset in the EXIF still points at the same thing.
    static func removingImageDescription(fromEXIF input: [UInt8]) throws -> [UInt8]? {
        var exif = input
        guard exif.count >= 8 else { throw Failure.malformed("Its EXIF is cut short.") }
        let big: Bool
        switch (exif[0], exif[1]) {
        case (0x4D, 0x4D): big = true
        case (0x49, 0x49): big = false
        default: throw Failure.malformed("Its EXIF has an unknown byte order.")
        }
        guard readUInt16(exif, 2, bigEndian: big) == 42 else { throw Failure.malformed("Its EXIF isn't TIFF data.") }
        let ifd = Int(readUInt32(exif, 4, bigEndian: big))
        guard ifd >= 8, ifd + 2 <= exif.count else { throw Failure.malformed("Its EXIF's first directory is missing.") }
        let count = Int(readUInt16(exif, ifd, bigEndian: big))
        let entries = ifd + 2
        let tableEnd = entries + count * 12 + 4  // with the next directory's offset
        guard tableEnd <= exif.count else { throw Failure.malformed("Its EXIF's first directory is cut short.") }

        guard let index = (0..<count).first(where: { readUInt16(exif, entries + $0 * 12, bigEndian: big) == 0x010E }) else {
            return nil
        }
        let entry = entries + index * 12
        var blanked = 0..<0
        // The text, when it's too long to sit in the entry itself.
        let type = readUInt16(exif, entry + 2, bigEndian: big)
        let valueCount = Int(readUInt32(exif, entry + 4, bigEndian: big))
        if let size = typeSizes[type] {
            let (byteCount, overflow) = valueCount.multipliedReportingOverflow(by: size)
            if !overflow, byteCount > 4 {
                let offset = Int(readUInt32(exif, entry + 8, bigEndian: big))
                let (end, past) = offset.addingReportingOverflow(byteCount)
                guard !past, offset >= 8, end <= exif.count, end <= ifd || offset >= tableEnd else {
                    throw Failure.malformed("Its EXIF description is out of place.")
                }
                // Blanked only when nothing else uses those bytes: another entry's value, or a directory it points
                // to. An orphaned string is harmless; zeroing bytes still in use isn't (#76's review).
                if !usedElsewhere(offset..<end, in: exif, ifd: ifd, count: count, except: index, bigEndian: big) {
                    for i in offset..<end { exif[i] = 0 }
                    blanked = offset..<end
                }
            }
        }
        // The entries after it, and the next directory's offset, move up one place.
        exif.replaceSubrange(entry..<(tableEnd - 12), with: exif[(entry + 12)..<tableEnd])
        for i in (tableEnd - 12)..<tableEnd { exif[i] = 0 }
        let newCount = UInt16(count - 1)
        exif.replaceSubrange(ifd..<(ifd + 2), with: big ? [UInt8(newCount >> 8), UInt8(newCount & 0xFF)]
                                                       : [UInt8(newCount & 0xFF), UInt8(newCount >> 8)])
        // A last look (#111): only IFD0's table and the old text may differ, so a mistake here can never reach the
        // maker note, the thumbnail or any other value.
        guard exif.count == input.count,
              exif.indices.allSatisfy({ exif[$0] == input[$0] || (ifd..<tableEnd).contains($0) || blanked.contains($0) })
        else { throw Failure.malformed("Its EXIF description couldn't be removed safely.") }
        return exif
    }

    /// The size of one value of each TIFF type, by its number.
    static let typeSizes: [UInt16: Int] = [1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8, 11: 4, 12: 8]

    /// Whether `new` is `old` with IFD0's ImageDescription taken out and nothing else changed (#111): the same size,
    /// IFD0's table holding the same entries less that one, in the same order, with the same next directory's offset
    /// and zeros where its last entry was, and the old text either kept or zeroed. Every other byte is the same, so the
    /// maker note, the thumbnail and every other value are untouched. It's worked out from `old` alone, apart from
    /// `removingImageDescription`, so the JPEG and PNG writers' last look would catch a mistake there.
    static func onlyImageDescriptionRemoved(from old: [UInt8], to new: [UInt8]) -> Bool {
        guard old.count == new.count, old.count >= 8 else { return false }
        let big: Bool
        switch (old[0], old[1]) {
        case (0x4D, 0x4D): big = true
        case (0x49, 0x49): big = false
        default: return false
        }
        let ifd = Int(readUInt32(old, 4, bigEndian: big))
        guard ifd >= 8, ifd + 2 <= old.count else { return false }
        let count = Int(readUInt16(old, ifd, bigEndian: big))
        let entries = ifd + 2
        let tableEnd = entries + count * 12 + 4
        guard tableEnd <= old.count,
              let index = (0..<count).first(where: { readUInt16(old, entries + $0 * 12, bigEndian: big) == 0x010E })
        else { return false }

        // IFD0's table as it should now be.
        let newCount = UInt16(count - 1)
        var table: [UInt8] = big ? [UInt8(newCount >> 8), UInt8(newCount & 0xFF)]
                                 : [UInt8(newCount & 0xFF), UInt8(newCount >> 8)]
        for i in 0..<count where i != index { table += old[(entries + i * 12)..<(entries + i * 12 + 12)] }
        table += old[(tableEnd - 4)..<tableEnd]
        table += [UInt8](repeating: 0, count: 12)
        guard Array(new[ifd..<tableEnd]) == table else { return false }

        // The old text, when it was too long to sit in the entry: kept, or zeroed.
        let entry = entries + index * 12
        var text = 0..<0
        if let size = typeSizes[readUInt16(old, entry + 2, bigEndian: big)] {
            let (bytes, overflow) = Int(readUInt32(old, entry + 4, bigEndian: big)).multipliedReportingOverflow(by: size)
            let offset = Int(readUInt32(old, entry + 8, bigEndian: big))
            let (end, past) = offset.addingReportingOverflow(bytes)
            if !overflow, !past, bytes > 4, end <= old.count { text = offset..<end }
        }
        return old.indices.allSatisfy { i in
            old[i] == new[i] || (ifd..<tableEnd).contains(i) || (text.contains(i) && new[i] == 0)
        }
    }

    /// Whether anything else in the EXIF overlaps `range`: another IFD0 entry's out-of-line value, or a directory
    /// reached from IFD0 (Exif, GPS, Interop, the next IFD with the embedded thumbnail), its values, or the thumbnail.
    static func usedElsewhere(_ range: Range<Int>, in exif: [UInt8], ifd: Int, count: Int, except skip: Int,
                              bigEndian big: Bool) -> Bool {
        let pointerTags: Set<UInt16> = [0x8769, 0x8825, 0xA005]
        var used: [Range<Int>] = []
        var visited: Set<Int> = [ifd]
        // Too many directories to follow (a loop, or a damaged file): the bytes are taken to be in use.
        var gaveUp = false
        func clamp(_ start: Int, _ length: Int) {
            guard start >= 0, start < exif.count, length > 0 else { return }
            used.append(start..<(start + min(length, exif.count - start)))
        }
        /// The directory's table, its entries' values and the directories they lead to; IFD0's own entry `skip` aside.
        func directory(at offset: Int, skipping skip: Int? = nil) {
            guard offset >= 8, offset + 2 <= exif.count else { return }
            guard visited.count < 16 else {
                gaveUp = true
                return
            }
            if offset != ifd { guard visited.insert(offset).inserted else { return } }
            let n = Int(readUInt16(exif, offset, bigEndian: big))
            let tableEnd = offset + 2 + n * 12
            clamp(offset, 2 + n * 12 + 4)
            // Its entries are followed even when the next directory's offset after them is missing, as some writers
            // leave it out of the Exif directory, and when the table itself is cut short, as far as whole entries go
            // (#111).
            let whole = min(n, (exif.count - offset - 2) / 12)
            var thumbnail: (at: Int?, length: Int?) = (nil, nil)
            for i in 0..<whole where i != skip {
                let entry = offset + 2 + i * 12
                let tag = readUInt16(exif, entry, bigEndian: big)
                let value = Int(readUInt32(exif, entry + 8, bigEndian: big))
                if pointerTags.contains(tag) { directory(at: value); continue }
                if tag == 0x0201 { thumbnail.at = value }
                if tag == 0x0202 { thumbnail.length = value }
                guard let size = typeSizes[readUInt16(exif, entry + 2, bigEndian: big)] else { continue }
                let (bytes, overflow) = Int(readUInt32(exif, entry + 4, bigEndian: big)).multipliedReportingOverflow(by: size)
                if !overflow, bytes > 4 { clamp(value, bytes) }
            }
            if let at = thumbnail.at, let length = thumbnail.length { clamp(at, length) }
            if whole == n, tableEnd + 4 <= exif.count { directory(at: Int(readUInt32(exif, tableEnd, bigEndian: big))) }
        }
        directory(at: ifd, skipping: skip)
        // IFD0's own table is where the entry is removed, which is never "used elsewhere".
        return gaveUp || used.dropFirst().contains { $0.overlaps(range) }
    }

    // MARK: - Bytes

    static func crc(_ bytes: [UInt8]) -> UInt32 {
        bytes.withUnsafeBufferPointer { UInt32(zlib.crc32(0, $0.baseAddress, uInt($0.count))) }
    }

    static func bigEndian(_ value: UInt32) -> [UInt8] {
        [UInt8(value >> 24), UInt8((value >> 16) & 0xFF), UInt8((value >> 8) & 0xFF), UInt8(value & 0xFF)]
    }

    private static func readUInt16(_ bytes: [UInt8], _ at: Int, bigEndian: Bool) -> UInt16 {
        let a = UInt16(bytes[at]), b = UInt16(bytes[at + 1])
        return bigEndian ? a << 8 | b : b << 8 | a
    }

    private static func readUInt32(_ bytes: [UInt8], _ at: Int, bigEndian: Bool) -> UInt32 {
        let b = (0..<4).map { UInt32(bytes[at + $0]) }
        return bigEndian ? b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3] : b[3] << 24 | b[2] << 16 | b[1] << 8 | b[0]
    }
}
