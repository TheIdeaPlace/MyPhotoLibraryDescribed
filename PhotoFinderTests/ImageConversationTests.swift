import XCTest
import ImageIO
@testable import Photo_Finder

#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older

/// What Get Info says when Apple Intelligence fails, and when it tries again (#237).
@available(macOS 27, *)
@MainActor
final class ImageConversationTests: XCTestCase {
    func testAFailedQuestionSaysItCouldntAnswerWithoutTheFrameworksNumber() {
        let error = NSError(domain: "ModelManagerServices.ModelManagerError", code: 1001)
        XCTAssertEqual(ImageConversation.message(for: error, asking: true),
                       "Apple Intelligence couldn't answer that. Try asking again in a moment.")
        XCTAssertTrue(ImageConversation.message(for: error).hasPrefix("Apple Intelligence couldn't describe this image"))
        XCTAssertEqual(ImageConversation.failureCode(for: error), "ModelManagerServices.ModelManagerError 1001")
    }

    func testAnUnexplainedFailureIsTriedAgainButNotACancellation() {
        XCTAssertTrue(ImageConversation.worthRetrying(NSError(domain: "ModelManagerServices.ModelManagerError", code: 1001)))
        XCTAssertFalse(ImageConversation.worthRetrying(CancellationError()))
    }

    func testTheSmallerTryDecodesAt1536Pixels() throws {
        let context = try XCTUnwrap(CGContext(data: nil, width: 3000, height: 2000, bitsPerComponent: 8, bytesPerRow: 0,
                                              space: CGColorSpaceCreateDeviceRGB(),
                                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        let data = NSMutableData()
        let destination = try XCTUnwrap(CGImageDestinationCreateWithData(data, "public.jpeg" as CFString, 1, nil))
        CGImageDestinationAddImage(destination, try XCTUnwrap(context.makeImage()), nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        XCTAssertEqual(ImageConversation.makeImage(from: data as Data)?.width, 2048)
        XCTAssertEqual(ImageConversation.makeImage(from: data as Data, maxPixelSize: 1536)?.width, 1536)
    }
}
#endif
