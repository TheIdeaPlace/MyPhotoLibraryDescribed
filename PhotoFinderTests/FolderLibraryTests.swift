import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import Photo_Finder

final class FolderLibraryTests: XCTestCase {
    private var temp: URL!

    override func setUpWithError() throws {
        // Under /var, a link to /private/var, which is how folders reached through a link are read too.
        temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: temp)
    }

    private func touch(_ relative: String, bytes: Int = 3) throws {
        let url = temp.appendingPathComponent(relative)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(repeating: 1, count: bytes).write(to: url)
    }

    // MARK: - Which files count

    func testTheSamePhotoAndVideoFilesAsWindows() {
        XCTAssertTrue(MediaFiles.isMedia("IMG_1.JPG"))
        XCTAssertTrue(MediaFiles.isMedia("a.heic"))
        XCTAssertTrue(MediaFiles.isMedia("a.cr3"))
        XCTAssertTrue(MediaFiles.isMedia("clip.mov"))
        XCTAssertFalse(MediaFiles.isMedia("notes.txt"))
        XCTAssertFalse(MediaFiles.isMedia("jpg"))
        XCTAssertTrue(MediaFiles.isVideo("clip.MP4"))
        XCTAssertFalse(MediaFiles.isVideo("a.png"))
    }

    func testKindsAreWorkedOutFromTheFileAsOnWindows() {
        XCTAssertEqual(MediaFiles.kind(name: "IMG_1.jpg", width: 4000, height: 3000), "Photo")
        XCTAssertEqual(MediaFiles.kind(name: "clip.mov", width: 0, height: 0), "Video")
        XCTAssertEqual(MediaFiles.kind(name: "Screen Recording 2026-10-11.mov", width: 0, height: 0), "Screen Recording")
        XCTAssertEqual(MediaFiles.kind(name: "Screenshot 2026-10-11 at 9.00.png", width: 10, height: 10), "Screenshot")
        XCTAssertEqual(MediaFiles.kind(name: "Screen Shot 2019-01-01.png", width: 10, height: 10), "Screenshot")
        XCTAssertEqual(MediaFiles.kind(name: "DSC_1.NEF", width: 6000, height: 4000), "RAW Photo")
        XCTAssertEqual(MediaFiles.kind(name: "dance.gif", width: 100, height: 100), "Animated Image")
        XCTAssertEqual(MediaFiles.kind(name: "wide.jpg", width: 10000, height: 2000), "Panorama")
    }

    // MARK: - Description keys

    func testAFileIsKeyedByPathSizeAndModifiedTimeInDotNetTicks() {
        let epoch = Date(timeIntervalSince1970: 0)
        XCTAssertEqual(FileDescriptionKey.ticks(epoch), 621_355_968_000_000_000)
        XCTAssertEqual(FileDescriptionKey.ticks(Date(timeIntervalSince1970: 1.5)), 621_355_968_015_000_000)
        XCTAssertEqual(FileDescriptionKey.key(path: "/Pictures/a.jpg", size: 2048, modified: epoch),
                       "file:/Pictures/a.jpg|2048|621355968000000000")
    }

    func testAnEditedFileGetsANewKeyAndAnUntouchedOneKeepsIt() {
        let date = Date(timeIntervalSince1970: 1_760_000_000.123456)
        let file = MediaFile(path: "/p/a.jpg", size: 10, created: nil, modified: date)
        XCTAssertEqual(file.descriptionKey, MediaFile(path: "/p/a.jpg", size: 10, created: nil, modified: date).descriptionKey)
        XCTAssertNotEqual(file.descriptionKey, MediaFile(path: "/p/a.jpg", size: 11, created: nil, modified: date).descriptionKey)
        XCTAssertNotEqual(file.descriptionKey,
                          MediaFile(path: "/p/a.jpg", size: 10, created: nil, modified: date.addingTimeInterval(1)).descriptionKey)
    }

    // MARK: - The folder list

    func testFoldersAreWrittenTheSameWayEachTime() {
        XCTAssertEqual(FolderRoots.normalize("/Users/k/Pictures/"), "/Users/k/Pictures")
        XCTAssertEqual(FolderRoots.normalize("/Users/k/Pictures/../Pictures"), "/Users/k/Pictures")
        XCTAssertEqual(FolderRoots.normalize("/"), "/")
    }

    func testAddingAFolderAlreadyInTheLibraryChangesNothing() {
        let roots = ["/Users/k/Pictures"]
        XCTAssertFalse(FolderRoots.adding("/Users/k/Pictures/", to: roots).added)
        XCTAssertFalse(FolderRoots.adding("/users/K/pictures", to: roots).added)
        XCTAssertFalse(FolderRoots.adding("/Users/k/Pictures/Trips", to: roots).added)
        XCTAssertEqual(FolderRoots.adding("/Users/k/Pictures/Trips", to: roots).roots, roots)
    }

    func testAddingAFolderHoldingAddedOnesTakesTheirPlace() {
        let result = FolderRoots.adding("/Users/k", to: ["/Users/k/Pictures", "/Volumes/Photos", "/Users/k/Movies"])
        XCTAssertTrue(result.added)
        XCTAssertEqual(result.roots, ["/Volumes/Photos", "/Users/k"])
    }

    func testAFolderBesideAnotherWithALongerNameIsNotInsideIt() {
        XCTAssertTrue(FolderRoots.adding("/Users/k/Pictures 2", to: ["/Users/k/Pictures"]).added)
        XCTAssertFalse(FolderRoots.isInside("/Users/k/Pictures 2", "/Users/k/Pictures"))
        XCTAssertTrue(FolderRoots.isInside("/Users/k/Pictures/2", "/Users/k/Pictures"))
    }

    func testRemovingAFolderAndTheFolderAPlaceIsIn() {
        let roots = ["/a", "/b"]
        XCTAssertEqual(FolderRoots.removing("/A", from: roots), ["/b"])
        XCTAssertEqual(FolderRoots.root(containing: "/b/c/d", in: roots), "/b")
        XCTAssertEqual(FolderRoots.root(containing: "/b", in: roots), "/b")
        XCTAssertNil(FolderRoots.root(containing: "/c", in: roots))
    }

    func testTheFolderListIsRemembered() throws {
        let suite = "FolderLibraryTests-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        XCTAssertEqual(FolderRoots.load(from: defaults), [])
        FolderRoots.save(["/a", "/b"], to: defaults)
        XCTAssertEqual(FolderRoots.load(from: defaults), ["/a", "/b"])
        // A list saved with overlaps (edited by hand, say) comes back tidy.
        defaults.set(["/a/", "/a/b", "/c"], forKey: FolderRoots.defaultsKey)
        XCTAssertEqual(FolderRoots.load(from: defaults), ["/a", "/c"])
    }

    func testAFolderIsNamedForItsLastPart() {
        XCTAssertEqual(FolderRoots.displayName("/Users/k/Pictures"), "Pictures")
        XCTAssertEqual(FolderRoots.displayName("/"), "/")
    }

    // MARK: - The Folders section

    func testTheTreeShowsOnlyFoldersWithPhotosAndCountsEverythingBelow() {
        let tree = FolderScanner.tree(root: "/r", mediaPaths: [
            "/r/a.jpg", "/r/Trips/b.jpg", "/r/Trips/Rome/c.jpg", "/r/Trips/Rome/d.mov", "/r/Art/e.png", "/elsewhere/f.jpg",
        ])
        XCTAssertEqual(tree.path, "/r")
        XCTAssertEqual(tree.count, 5)
        XCTAssertEqual(tree.children.map(\.path), ["/r/Art", "/r/Trips"])
        XCTAssertEqual(tree.children[1].count, 3)
        XCTAssertEqual(tree.children[1].children.map(\.path), ["/r/Trips/Rome"])
        XCTAssertEqual(tree.children[1].children[0].count, 2)
    }

    func testAnEmptyFolderIsStillShownSoItCanBeRemoved() {
        let tree = FolderScanner.tree(root: "/r", mediaPaths: [])
        XCTAssertEqual(tree, FolderNode(path: "/r", count: 0, children: []))
    }

    func testSidebarItemsForFolders() throws {
        let rome = FolderNode(path: "/r/Trips/Rome", count: 2, children: [])
        let trips = FolderNode(path: "/r/Trips", count: 3, children: [rome])
        let items = FolderScanner.sidebarItems([FolderNode(path: "/r", count: 4, children: [trips])])
        let root = try XCTUnwrap(items.first)
        XCTAssertEqual(root.id, "dir:/r")
        XCTAssertEqual(root.title, "r")
        XCTAssertEqual(root.directoryPath, "/r")
        XCTAssertNil(root.parentID)
        XCTAssertEqual(root.containerKind, "Folder")
        let child = try XCTUnwrap(root.children?.first)
        XCTAssertEqual(child.title, "Trips")
        XCTAssertEqual(child.parentID, "dir:/r")
        XCTAssertEqual(child.count, 3)
        // A folder with no subfolders has no disclosure triangle.
        XCTAssertNil(child.children?.first?.children)
    }

    func testFoldersWithTheSameNameAreShownByPath() {
        let items = FolderScanner.sidebarItems([
            FolderNode(path: "/Volumes/A/Photos", count: 0, children: []),
            FolderNode(path: "/Volumes/B/Photos", count: 0, children: []),
            FolderNode(path: "/Volumes/B/Other", count: 0, children: []),
        ])
        XCTAssertEqual(items.map(\.title), ["/Volumes/A/Photos", "/Volumes/B/Photos", "Other"])
    }

    // MARK: - Reading the disk

    func testScanningSkipsHiddenFilesOtherFilesAndPackages() throws {
        try touch("a.jpg")
        try touch("notes.txt")
        try touch(".hidden.jpg")
        try touch("Trips/b.heic")
        try touch("Photos Library.photoslibrary/originals/c.jpg")
        try touch("Some.app/Contents/d.png")
        let root = temp.path
        let found = Set(FolderScanner.mediaPaths(under: root))
        XCTAssertEqual(found, [root + "/a.jpg", root + "/Trips/b.heic"])
        let tree = FolderScanner.scan(root)
        XCTAssertEqual(tree.count, 2)
        XCTAssertEqual(tree.children.map(\.path), [root + "/Trips"])
    }

    func testAFolderListsOnlyItsOwnPhotosAndVideos() throws {
        try touch("a.jpg", bytes: 5)
        try touch("b.mov", bytes: 7)
        try touch("notes.txt")
        try touch("Trips/c.jpg")
        let files = FolderScanner.files(in: temp.path).sorted { $0.name < $1.name }
        XCTAssertEqual(files.map(\.name), ["a.jpg", "b.mov"])
        XCTAssertEqual(files.map(\.size), [5, 7])
        XCTAssertEqual(files[0].path, temp.path + "/a.jpg")
        XCTAssertTrue(files[1].isVideo)
    }

    // MARK: - Metadata

    func testExifDates() throws {
        let local = try XCTUnwrap(FileMetadata.exifDate("2024:06:01 14:30:00"))
        let parts = Calendar.current.dateComponents([.year, .month, .day, .hour, .minute], from: local)
        XCTAssertEqual([parts.year, parts.month, parts.day, parts.hour, parts.minute], [2024, 6, 1, 14, 30])
        let offset = try XCTUnwrap(FileMetadata.exifDate("2024:06:01 14:30:00", offset: "+02:00"))
        XCTAssertEqual(offset, ISO8601DateFormatter().date(from: "2024-06-01T12:30:00Z"))
        XCTAssertNil(FileMetadata.exifDate("not a date"))
    }

    func testMetadataFromImageProperties() {
        let properties: [CFString: Any] = [
            kCGImagePropertyPixelWidth: 4000,
            kCGImagePropertyPixelHeight: 3000,
            kCGImagePropertyOrientation: 6,
            kCGImagePropertyTIFFDictionary: [kCGImagePropertyTIFFImageDescription: "From TIFF"],
            kCGImagePropertyIPTCDictionary: [kCGImagePropertyIPTCCaptionAbstract: "  A dog on a beach ",
                                             kCGImagePropertyIPTCKeywords: ["dog", " beach", ""]],
            kCGImagePropertyGPSDictionary: [kCGImagePropertyGPSLatitude: 43.07, kCGImagePropertyGPSLatitudeRef: "N",
                                            kCGImagePropertyGPSLongitude: 89.4, kCGImagePropertyGPSLongitudeRef: "W"],
        ]
        let metadata = FileMetadata.from(properties: properties, xmp: nil)
        // Turned a quarter: shown 3000 wide.
        XCTAssertEqual(metadata.width, 3000)
        XCTAssertEqual(metadata.height, 4000)
        XCTAssertEqual(metadata.caption, "A dog on a beach")
        XCTAssertEqual(metadata.keywords, ["dog", "beach"])
        XCTAssertEqual(metadata.latitude, 43.07)
        XCTAssertEqual(metadata.longitude, -89.4)
        XCTAssertNil(metadata.dateTaken)
    }

    func testTheCaptionFallsBackToTheTIFFDescription() {
        let properties: [CFString: Any] = [kCGImagePropertyTIFFDictionary: [kCGImagePropertyTIFFImageDescription: "From TIFF"]]
        XCTAssertEqual(FileMetadata.from(properties: properties, xmp: nil).caption, "From TIFF")
    }

    func testReadingAPhotoWrittenWithACaptionAndDate() throws {
        let url = temp.appendingPathComponent("captioned.jpg")
        let context = try XCTUnwrap(CGContext(data: nil, width: 40, height: 20, bitsPerComponent: 8, bytesPerRow: 0,
                                              space: CGColorSpaceCreateDeviceRGB(),
                                              bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        context.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 40, height: 20))
        let image = try XCTUnwrap(context.makeImage())
        let destination = try XCTUnwrap(CGImageDestinationCreateWithURL(url as CFURL, UTType.jpeg.identifier as CFString, 1, nil))
        let properties: [CFString: Any] = [
            kCGImagePropertyExifDictionary: [kCGImagePropertyExifDateTimeOriginal: "2020:01:02 03:04:05"],
            kCGImagePropertyIPTCDictionary: [kCGImagePropertyIPTCCaptionAbstract: "A red rectangle",
                                             kCGImagePropertyIPTCKeywords: ["red"]],
        ]
        CGImageDestinationAddImage(destination, image, properties as CFDictionary)
        XCTAssertTrue(CGImageDestinationFinalize(destination))

        let metadata = FileMetadata.read(url)
        XCTAssertEqual(metadata.width, 40)
        XCTAssertEqual(metadata.height, 20)
        XCTAssertEqual(metadata.caption, "A red rectangle")
        XCTAssertEqual(metadata.keywords, ["red"])
        XCTAssertEqual(metadata.dateTaken, FileMetadata.exifDate("2020:01:02 03:04:05"))

        let file = try XCTUnwrap(FolderScanner.files(in: temp.path).first)
        let row = FileRow(file: file, metadata: metadata)
        XCTAssertEqual(row.id, "file:" + url.path)
        XCTAssertEqual(row.name, "captioned.jpg")
        XCTAssertEqual(row.kind, "Photo")
        XCTAssertEqual(row.dimensions, "40 × 20")
        XCTAssertEqual(row.orientation, "Landscape")
        XCTAssertEqual(row.caption, "A red rectangle")
        XCTAssertEqual(row.date, metadata.dateTaken)
        XCTAssertEqual(row.source, temp.lastPathComponent)
        XCTAssertFalse(row.isContainer)
    }

    func testAFileRowBeforeItsMetadataIsRead() {
        let created = Date(timeIntervalSince1970: 1_000)
        let file = MediaFile(path: "/p/Trips/clip.mov", size: 1, created: created, modified: Date(timeIntervalSince1970: 2_000))
        let row = FileRow(file: file, metadata: nil)
        XCTAssertEqual(row.kind, "Video")
        XCTAssertEqual(row.date, created)
        XCTAssertEqual(row.symbol, "video")
        XCTAssertEqual(row.source, "Trips")
        XCTAssertEqual(row.caption, "")
        XCTAssertEqual(row.dimensions, "")
    }
}
