import UniformTypeIdentifiers
import XCTest
@testable import Photo_Finder

final class ReadOnlyTests: XCTestCase {
    override func tearDown() {
        PhotoChanges.allowed = false
    }

    func testChangesAreNotAllowedUntilAgreed() {
        PhotoChanges.allowed = false
        XCTAssertFalse(PhotoChanges.allowed)
        XCTAssertEqual(PhotoChanges.ReadOnly().errorDescription, PhotoChanges.readOnlyMessage)
        XCTAssertTrue(PhotoChanges.readOnlyMessage.hasSuffix("turn off Read-Only Mode in the File menu."))
    }

    func testTheFileWriterRefusesWhileReadOnlyAndLeavesTheFileAlone() throws {
        PhotoChanges.allowed = false
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".jpg")
        defer { try? FileManager.default.removeItem(at: url) }
        let context = try XCTUnwrap(CGContext(data: nil, width: 8, height: 8, bitsPerComponent: 8, bytesPerRow: 0,
                                              space: CGColorSpaceCreateDeviceRGB(),
                                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        let destination = try XCTUnwrap(CGImageDestinationCreateWithURL(url as CFURL, UTType.jpeg.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, try XCTUnwrap(context.makeImage()), nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        let before = try Data(contentsOf: url)
        XCTAssertThrowsError(try FileMetadataWriter.write(.init(caption: "A dog"), to: url)) { error in
            XCTAssertTrue(error is PhotoChanges.ReadOnly)
        }
        XCTAssertEqual(try Data(contentsOf: url), before)
    }

    func testLibraryChangesAreRefusedWhileReadOnly() async {
        PhotoChanges.allowed = false
        let ran = expectation(description: "the change block runs")
        ran.isInverted = true
        do {
            try await PhotoChanges.performLibraryChanges { ran.fulfill() }
            XCTFail("should have refused")
        } catch {
            XCTAssertTrue(error is PhotoChanges.ReadOnly)
        }
        await fulfillment(of: [ran], timeout: 0.2)
    }
}
