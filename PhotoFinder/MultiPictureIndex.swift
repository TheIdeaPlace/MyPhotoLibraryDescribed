import Foundation

/// A JPEG's Multi-Picture index (CIPA DC-007, the APP2 "MPF\0" segment), as Ultra HDR, Apple's HDR photos and MPO files
/// carry it for their gain map or other view (#90). It's a small TIFF structure: an MP Entry for each picture, with its
/// size and where it starts counted from the index's own TIFF header (the first picture starts at 0, the file's start).
/// Changing the XMP changes the first picture's size and, when the XMP comes after the index, where the others start.
///
/// The same approach as the Windows app's MultiPictureIndex (#150). Anything it can't follow, before or after the
/// change, throws `FileMetadataWriteError.multiPicture`.
struct MultiPictureIndex {
    static let signature: [UInt8] = Array("MPF".utf8) + [0]
    private static let mpEntryTag = 0xB002

    let littleEndian: Bool
    /// Where the TIFF header (the byte order mark) is in the file.
    let header: Int
    /// Where the MP Entries start in the file.
    let entries: Int
    let sizes: [Int]
    let offsets: [Int]

    static func isIndex(_ segment: JPEGMetadata.Segment) -> Bool {
        segment.marker == 0xE2 && segment.starts(with: signature)
    }

    /// The index whose TIFF header is at `header`, in a segment ending at `segmentEnd`, checked against the file: every
    /// picture after the first starts with a JPEG's start marker and fits in the file.
    static func read(_ bytes: [UInt8], header: Int, segmentEnd: Int) throws -> MultiPictureIndex {
        let refused = FileMetadataWriteError.multiPicture
        let little: Bool
        if has(bytes, at: header, [0x49, 0x49, 0x2A, 0x00]) {
            little = true
        } else if has(bytes, at: header, [0x4D, 0x4D, 0x00, 0x2A]) {
            little = false
        } else {
            throw refused
        }
        let end = min(segmentEnd, bytes.count)
        func u16(_ at: Int) throws -> Int {
            guard at >= 0, at + 2 <= end else { throw refused }
            return Int(value(bytes, at: at, count: 2, little: little))
        }
        func u32(_ at: Int) throws -> Int {
            guard at >= 0, at + 4 <= end else { throw refused }
            return Int(value(bytes, at: at, count: 4, little: little))
        }

        let ifd = header + (try u32(header + 4))
        let tags = try u16(ifd)
        for t in 0..<tags {
            let tag = ifd + 2 + t * 12
            guard try u16(tag) == mpEntryTag else { continue }
            // UNDEFINED, 16 bytes a picture, at an offset from the header.
            let length = try u32(tag + 4)
            guard try u16(tag + 2) == 7, length > 0, length % 16 == 0, length <= 16 * 64 else { throw refused }
            let entries = header + (try u32(tag + 8))
            guard entries + length <= end else { throw refused }
            let count = length / 16
            let sizes = try (0..<count).map { try u32(entries + $0 * 16 + 4) }
            let offsets = try (0..<count).map { try u32(entries + $0 * 16 + 8) }
            try check(bytes, header: header, sizes: sizes, offsets: offsets)
            return MultiPictureIndex(littleEndian: little, header: header, entries: entries, sizes: sizes, offsets: offsets)
        }
        throw refused
    }

    /// Patches the index in `changed`, the file after the segments before its picture grew by `delta` bytes (or shrank),
    /// then checks it still points at the pictures. `moved` says whether the change came before the index, so the index
    /// moved with the pictures after it and only the first picture's size changes; otherwise the other pictures moved
    /// away from it too.
    func patch(_ changed: inout [UInt8], delta: Int, moved: Bool) throws {
        let shift = moved ? delta : 0
        var sizes = sizes, offsets = offsets
        sizes[0] += delta
        if !moved {
            for i in offsets.indices.dropFirst() { offsets[i] += delta }
        }
        guard (sizes + offsets).allSatisfy({ (0...Int(UInt32.max)).contains($0) }),
              entries + shift + sizes.count * 16 <= changed.count else { throw FileMetadataWriteError.multiPicture }
        for i in sizes.indices {
            Self.write(&changed, at: entries + shift + i * 16 + 4, UInt32(sizes[i]), little: littleEndian)
            Self.write(&changed, at: entries + shift + i * 16 + 8, UInt32(offsets[i]), little: littleEndian)
        }
        try Self.check(changed, header: header + shift, sizes: sizes, offsets: offsets)
    }

    /// Where the other pictures start is what readers use to find them, so that must be exact. The first picture's
    /// size needn't be: cameras record it a little off either way (Apple's HDR sample 70 bytes long, into the gain map;
    /// Pixel's Ultra HDR photos about 300 bytes short), so it's only kept in step, past the index and within the file.
    private static func check(_ bytes: [UInt8], header: Int, sizes: [Int], offsets: [Int]) throws {
        guard offsets[0] == 0, sizes[0] > header, sizes[0] <= bytes.count else { throw FileMetadataWriteError.multiPicture }
        for i in offsets.indices.dropFirst() {
            let start = header + offsets[i]
            guard offsets[i] != 0, start + sizes[i] <= bytes.count, has(bytes, at: start, [0xFF, 0xD8, 0xFF]) else {
                throw FileMetadataWriteError.multiPicture
            }
        }
    }

    private static func has(_ bytes: [UInt8], at: Int, _ signature: [UInt8]) -> Bool {
        at >= 0 && at + signature.count <= bytes.count && Array(bytes[at..<(at + signature.count)]) == signature
    }

    private static func value(_ bytes: [UInt8], at: Int, count: Int, little: Bool) -> UInt32 {
        (0..<count).reduce(UInt32(0)) { value, i in
            value | UInt32(bytes[at + i]) << UInt32(little ? 8 * i : 8 * (count - 1 - i))
        }
    }

    private static func write(_ bytes: inout [UInt8], at: Int, _ value: UInt32, little: Bool) {
        for i in 0..<4 { bytes[at + i] = UInt8((value >> UInt32(little ? 8 * i : 8 * (3 - i))) & 0xFF) }
    }
}
