import XCTest
@testable import Photo_Finder

final class DescribeAgainTests: XCTestCase {
    private func makeCache() -> DescriptionCache {
        DescriptionCache(fileURL: FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString).appendingPathComponent("Descriptions.json"))
    }

    func testDescribingAgainReplacesTheSavedDescription() {
        let cache = makeCache()
        cache.remember("A dog", forKey: "photo")
        cache.remember("  A brown dog on a beach \n", forKey: "photo")
        XCTAssertEqual(cache.description(forKey: "photo"), "A brown dog on a beach")
    }

    func testAnEmptyDescriptionDoesNotReplaceOne() {
        let cache = makeCache()
        cache.remember("A dog", forKey: "photo")
        cache.remember("   ", forKey: "photo")
        XCTAssertEqual(cache.description(forKey: "photo"), "A dog")
    }

    func testSavingADescriptionTellsTheScreenToUpdate() {
        let cache = makeCache()
        let changed = expectation(forNotification: DescriptionCache.didChange, object: cache)
        cache.remember("A dog", forKey: "photo")
        wait(for: [changed], timeout: 1)
    }
}
