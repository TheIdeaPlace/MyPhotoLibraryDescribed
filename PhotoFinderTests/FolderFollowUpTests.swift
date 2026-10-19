import XCTest
@testable import Photo_Finder

/// The folder follow-ups (#47): where to go when a subfolder disappears, which photos to describe in the background
/// after a scan, and Get Info's General fields from the file's own metadata.
final class FolderFollowUpTests: XCTestCase {
    // MARK: - Falling back to a parent folder

    func testASubfolderThatDisappearsFallsBackToItsParent() {
        let listed: Set<String> = ["/Users/k/Pictures", "/Users/k/Pictures/Trips"]
        let parent = FolderRoots.nearestListed(above: "/Users/k/Pictures/Trips/Rome", roots: ["/Users/k/Pictures"],
                                               isListed: listed.contains)
        XCTAssertEqual(parent, "/Users/k/Pictures/Trips")
    }

    func testFallsBackPastParentsThatAreGoneToo() {
        let parent = FolderRoots.nearestListed(above: "/Users/k/Pictures/Trips/Rome/Day 1", roots: ["/Users/k/Pictures"],
                                               isListed: { $0 == "/Users/k/Pictures" })
        XCTAssertEqual(parent, "/Users/k/Pictures")
    }

    func testNoFallbackOutsideTheAddedFolders() {
        XCTAssertNil(FolderRoots.nearestListed(above: "/Users/k/Pictures/Trips", roots: ["/Volumes/Photos"],
                                               isListed: { _ in true }))
        // The added folder itself never falls back to the folder holding it.
        XCTAssertNil(FolderRoots.nearestListed(above: "/Users/k/Pictures", roots: ["/Users/k/Pictures"],
                                               isListed: { _ in true }))
        // Nothing above it listed (its added folder was removed): All Photos, decided by the caller.
        XCTAssertNil(FolderRoots.nearestListed(above: "/Users/k/Pictures/Trips", roots: ["/Users/k/Pictures"],
                                               isListed: { _ in false }))
    }

    // MARK: - Where to go instead, with and without Photos access

    func testAVanishedSubfolderGoesToItsParentWithOrWithoutPhotosAccess() {
        let listed: Set<String> = ["/Users/k/Pictures"]
        for allowed in [true, false] {
            XCTAssertEqual(FolderRoots.fallbackPlace(previousPath: "/Users/k/Pictures/Trips", roots: ["/Users/k/Pictures"],
                                                     photosAllowed: allowed, isListed: listed.contains),
                           FolderID.folder("/Users/k/Pictures"))
        }
    }

    func testWithPhotosAccessNothingAboveGoesToAllPhotos() {
        XCTAssertEqual(FolderRoots.fallbackPlace(previousPath: nil, roots: ["/Volumes/Photos"], photosAllowed: true,
                                                 isListed: { _ in true }),
                       SidebarID.allPhotos)
    }

    func testWithoutPhotosAccessGoesToTheFirstAddedFolderStillListed() {
        let place = FolderRoots.fallbackPlace(previousPath: "/Users/k/Pictures/Trips",
                                              roots: ["/Volumes/Gone", "/Users/k/Desktop"], photosAllowed: false,
                                              isListed: { $0 == "/Users/k/Desktop" })
        XCTAssertEqual(place, FolderID.folder("/Users/k/Desktop"))
    }

    func testWithoutPhotosAccessOrFoldersGoesToTheNoAccessRow() {
        XCTAssertEqual(FolderRoots.fallbackPlace(previousPath: nil, roots: [], photosAllowed: false, isListed: { _ in true }),
                       SidebarID.photosNoAccess)
        XCTAssertEqual(FolderRoots.fallbackPlace(previousPath: "/Users/k/Pictures/Trips", roots: ["/Users/k/Pictures"],
                                                 photosAllowed: false, isListed: { _ in false }),
                       SidebarID.photosNoAccess)
    }

    // MARK: - Which photos to describe in the background

    func testTheFirstScanAfterLaunchDescribesNothing() {
        XCTAssertEqual(FolderChanges.photosToDescribe(before: nil, after: ["/p/a.jpg"], justAdded: false), [])
    }

    func testAFolderJustAddedDescribesAllItsPhotosButNotVideos() {
        let found = FolderChanges.photosToDescribe(before: nil, after: ["/p/a.jpg", "/p/b.MOV", "/p/c.heic"], justAdded: true)
        XCTAssertEqual(found, ["/p/a.jpg", "/p/c.heic"])
    }

    func testOnlyPhotosAddedSinceTheLastScanAreDescribed() {
        let found = FolderChanges.photosToDescribe(before: ["/p/a.jpg", "/p/gone.jpg"],
                                                   after: ["/p/a.jpg", "/p/new.png", "/p/new.mp4", "/p/Sub/also.jpg"],
                                                   justAdded: false)
        XCTAssertEqual(found, ["/p/new.png", "/p/Sub/also.jpg"])
    }

    func testScanningFindsPhotosAddedSinceTheLastScan() throws {
        let temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: temp) }
        try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
        try Data([1]).write(to: temp.appendingPathComponent("a.jpg"))
        let root = temp.path
        let first = FolderScan.scan(root, before: nil, justAdded: false)
        XCTAssertEqual(first.paths, [root + "/a.jpg"])
        XCTAssertEqual(first.newPhotos, [])
        try Data([1]).write(to: temp.appendingPathComponent("b.jpg"))
        let second = FolderScan.scan(root, before: first.paths, justAdded: false)
        XCTAssertEqual(second.newPhotos, [root + "/b.jpg"])
        XCTAssertEqual(second.node.count, 2)
    }

    func testAMissingFolderHasNoListOfPhotos() {
        let scan = FolderScan.scan("/Volumes/Not Plugged In \(UUID().uuidString)", before: ["/x/a.jpg"], justAdded: true)
        XCTAssertNil(scan.paths)
        XCTAssertEqual(scan.newPhotos, [])
        XCTAssertEqual(scan.node.count, 0)
    }

    // MARK: - Get Info for a file

    func testGetInfoGeneralFieldsComeFromTheFilesMetadata() {
        let created = Date(timeIntervalSince1970: 1_700_000_000)
        let taken = Date(timeIntervalSince1970: 1_600_000_000)
        let file = MediaFile(path: "/p/a.jpg", size: 10, created: created, modified: created)
        var metadata = FileMetadata()
        metadata.dateTaken = taken
        metadata.width = 4000
        metadata.height = 3000
        metadata.caption = "A dog"
        metadata.keywords = ["dog", "park"]
        let fields = PhotoMetadata.general(for: file, row: FileRow(file: file, metadata: metadata))
        let byLabel = Dictionary(fields.map { ($0.label, $0.value) }, uniquingKeysWith: { a, _ in a })
        XCTAssertEqual(byLabel["Date Taken"], taken.formatted(date: .complete, time: .standard))
        XCTAssertEqual(byLabel["Dimensions"], "4000 × 3000 pixels")
        XCTAssertEqual(byLabel["Caption"], "A dog")
        XCTAssertEqual(byLabel["Keywords"], "dog, park")
    }
}
