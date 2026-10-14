import XCTest
@testable import Photo_Finder

/// Where each description came from (#199): the record saved with it, the file's versioned format, corrections keeping
/// Apple Intelligence's text, and each describe path saving its source and prompt. Each test has a temporary folder;
/// the real data folder and the Photos library are never touched.
final class DescriptionProvenanceTests: XCTestCase {
    private func makeURL() -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("DescriptionProvenanceTests-\(UUID().uuidString)")
        addTeardownBlock {
            try? FileManager.default.removeItem(at: folder)
            DispatchQueue.global().asyncAfter(deadline: .now() + 3) { try? FileManager.default.removeItem(at: folder) }
        }
        return folder.appendingPathComponent("Descriptions.json")
    }

    private let made = Date(timeIntervalSince1970: 1_791_000_000)  // 2026-10-03T04:00:00Z

    private func aiProvenance(_ prompt: String = "Describe this photo.") -> DescriptionProvenance {
        var provenance = DescriptionProvenance.appleIntelligence(prompt: prompt, seconds: 3.1234, model: "AFM 3 Core",
                                                                 locale: "en_US")
        provenance.os = "macOS 27.2 (26B5101f)"
        provenance.app = "0.2.0 (12)"
        provenance.created = made
        return provenance
    }

    // MARK: - The record

    func testARecordKeepsEverythingAcrossARestart() throws {
        let url = makeURL()
        let cache = DescriptionCache(fileURL: url)
        cache.remember("  A red mug on a table. \n", forKey: "photo", provenance: aiProvenance())
        cache.save()
        let record = try XCTUnwrap(DescriptionCache(fileURL: url).record(forKey: "photo"))
        XCTAssertEqual(record, DescriptionRecord(text: "A red mug on a table.", source: "apple-intelligence",
                                                 prompt: "Describe this photo.", model: "AFM 3 Core", locale: "en_US",
                                                 os: "macOS 27.2 (26B5101f)", app: "0.2.0 (12)",
                                                 created: "2026-10-03T04:00:00Z", durationMs: 3123))
        XCTAssertFalse(record.isEdited)
    }

    func testTheFileIsVersionedWithTheSharedFieldNames() throws {
        let url = makeURL()
        let cache = DescriptionCache(fileURL: url)
        cache.remember("A red mug.", forKey: "file:/USERS/K/A.JPG|10|20", provenance: aiProvenance())
        cache.save()
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        XCTAssertEqual(json["version"] as? Int, 2)
        XCTAssertEqual(Set(json.keys), ["version", "descriptions"])
        let descriptions = try XCTUnwrap(json["descriptions"] as? [String: [String: Any]])
        let record = try XCTUnwrap(descriptions["file:/USERS/K/A.JPG|10|20"], "the key is kept exactly")
        XCTAssertEqual(Set(record.keys), ["text", "source", "prompt", "model", "locale", "os", "app", "created", "durationMs"],
                       "what isn't known (edited, original) is left out")
        XCTAssertEqual(record["source"] as? String, "apple-intelligence")
        XCTAssertEqual(record["durationMs"] as? Int, 3123)
        XCTAssertEqual(record["created"] as? String, "2026-10-03T04:00:00Z")
    }

    func testTimesAreISO8601InUTC() {
        XCTAssertEqual(DescriptionProvenance.timestamp(Date(timeIntervalSince1970: 0)), "1970-01-01T00:00:00Z")
    }

    func testAppleIntelligencesProvenanceNamesThisMacAndApp() {
        let provenance = DescriptionProvenance.appleIntelligence(prompt: "Describe it.", seconds: 0.0004)
        XCTAssertEqual(provenance.source, "apple-intelligence")
        XCTAssertEqual(provenance.prompt, "Describe it.")
        XCTAssertEqual(provenance.durationMs, 0)
        XCTAssertEqual(provenance.app, AppInfo.version)
        XCTAssertTrue(provenance.os?.hasPrefix("macOS ") ?? false, provenance.os ?? "nil")
        XCTAssertFalse(provenance.model?.isEmpty ?? true)
        XCTAssertEqual(provenance.locale, Locale.current.identifier)
        XCTAssertNil(DescriptionProvenance.appleIntelligence(prompt: "x").durationMs, "not timed: left out")
    }

    // MARK: - Corrections and Redescribe

    func testACorrectionKeepsApplesTextAndHowItWasMade() throws {
        let url = makeURL()
        let cache = DescriptionCache(fileURL: url)
        cache.remember("A dog on sand.", forKey: "photo", provenance: aiProvenance())
        cache.remember("Rex on the beach.", forKey: "photo", edited: true)
        cache.remember("Rex, our dog, on the beach.", forKey: "photo", edited: true)
        cache.save()
        let record = try XCTUnwrap(DescriptionCache(fileURL: url).record(forKey: "photo"))
        XCTAssertEqual(record.text, "Rex, our dog, on the beach.")
        XCTAssertEqual(record.source, "person")
        XCTAssertTrue(record.isEdited)
        XCTAssertEqual(record.original, "A dog on sand.", "the first text Apple Intelligence gave, not the first correction")
        XCTAssertNotNil(record.edited)
        XCTAssertEqual(record.prompt, "Describe this photo.")
        XCTAssertEqual(record.model, "AFM 3 Core")
        XCTAssertEqual(record.created, "2026-10-03T04:00:00Z", "when it was described")
    }

    func testACorrectionIsDatedWhenItsMade() {
        let corrected = DescriptionCache.corrected(aiProvenance().record("A dog."), to: "Rex.",
                                                   at: Date(timeIntervalSince1970: 1_791_086_400))
        XCTAssertEqual(corrected.edited, "2026-10-04T04:00:00Z")
        XCTAssertEqual(corrected.original, "A dog.")
    }

    func testADescriptionThePersonWroteHasNoOriginalOrModel() throws {
        let cache = DescriptionCache(fileURL: makeURL())
        cache.remember("My own words.", forKey: "photo", edited: true)
        let record = try XCTUnwrap(cache.record(forKey: "photo"))
        XCTAssertEqual(record.source, "person")
        XCTAssertNil(record.original)
        XCTAssertNil(record.prompt)
        XCTAssertNil(record.model)
        XCTAssertEqual(record.created, record.edited)
        XCTAssertEqual(record.app, AppInfo.version)
    }

    func testRedescribingReplacesTheRecordWhole() throws {
        let cache = DescriptionCache(fileURL: makeURL())
        cache.remember("A dog.", forKey: "photo", provenance: aiProvenance())
        cache.remember("Rex.", forKey: "photo", edited: true)
        cache.remember("A brown dog.", forKey: "photo", provenance: aiProvenance("List what you see."))
        let record = try XCTUnwrap(cache.record(forKey: "photo"))
        XCTAssertEqual(record.text, "A brown dog.")
        XCTAssertEqual(record.source, "apple-intelligence")
        XCTAssertEqual(record.prompt, "List what you see.")
        XCTAssertNil(record.edited)
        XCTAssertNil(record.original)
        XCTAssertFalse(cache.isEdited(forKey: "photo"))
    }

    func testRekeyMovesTheWholeRecord() throws {
        let cache = DescriptionCache(fileURL: makeURL())
        cache.remember("A dog.", forKey: "old", provenance: aiProvenance())
        cache.remember("Rex.", forKey: "old", edited: true)
        let before = cache.record(forKey: "old")
        cache.rekey(from: "old", to: "new")
        XCTAssertNil(cache.record(forKey: "old"))
        XCTAssertEqual(cache.record(forKey: "new"), before)
    }

    // MARK: - Files from before #199

    func testAFlatFileIsReadWithItsDetailsUnknownAndSavedInTheNewForm() throws {
        let url = makeURL()
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(#"{"a":"A cat.","b":"Mine."}"#.utf8).write(to: url)
        try Data(#"["b"]"#.utf8).write(to: url.deletingLastPathComponent().appendingPathComponent("EditedDescriptions.json"))
        let cache = DescriptionCache(fileURL: url)
        XCTAssertEqual(cache.record(forKey: "a"), DescriptionRecord(text: "A cat."))
        XCTAssertEqual(cache.record(forKey: "b")?.source, "person", "a correction from before stays one")
        XCTAssertTrue(cache.isEdited(forKey: "b"))
        cache.save()
        let saved = try ProvenanceFile.records(at: url)
        XCTAssertEqual(saved["a"], DescriptionRecord(text: "A cat."))
        XCTAssertEqual(saved["b"]?.source, "person")
    }

    func testANewerVersionsRecordFieldsDontStopTheFileBeingRead() throws {
        let url = makeURL()
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(#"{"version":2,"descriptions":{"a":{"text":"A cat.","source":"windows-ai","kind":"Accessible"}}}"#.utf8)
            .write(to: url)
        let record = DescriptionCache(fileURL: url).record(forKey: "a")
        XCTAssertEqual(record?.text, "A cat.")
        XCTAssertEqual(record?.source, "windows-ai")
    }

    // MARK: - Each describe path

    @MainActor
    func testDescribePhotoSavesAppleIntelligenceAndTheStandardPrompt() async throws {
        let library = try await makeLibrary()
        library.describer.reply = "Two cats on a sofa"
        library.model.selection = [library.id("c.jpg")]
        library.model.describeFocusedPhoto()
        try await waitUntil("the description to be saved") { library.record(for: "c.jpg") != nil }
        let record = try XCTUnwrap(library.record(for: "c.jpg"))
        XCTAssertEqual(record.source, "apple-intelligence")
        XCTAssertEqual(record.prompt, DescriptionPrompt.standard)
        XCTAssertEqual(library.describer.prompts, [DescriptionPrompt.standard], "the prompt saved is the one asked")
        XCTAssertEqual(record.app, AppInfo.version)
        XCTAssertNotNil(record.created)
        XCTAssertNotNil(record.durationMs)
        XCTAssertNil(record.edited)
    }

    @MainActor
    func testYourOwnPromptIsSavedWithTheDescription() async throws {
        let library = try await makeLibrary(settings: [DescriptionPrompt.key: "  List the colours.  "])
        library.describer.reply = "Red and blue."
        library.model.selection = [library.id("c.jpg")]
        library.model.describeFocusedPhoto()
        try await waitUntil("the description to be saved") { library.record(for: "c.jpg") != nil }
        XCTAssertEqual(library.record(for: "c.jpg")?.prompt, "List the colours.")
        XCTAssertEqual(library.describer.prompts, ["List the colours."])
    }

    @MainActor
    func testRedescribeSavesANewRecord() async throws {
        let library = try await makeLibrary()
        library.remember("Old", for: "a.jpg")
        XCTAssertNil(library.record(for: "a.jpg")?.source, "saved without provenance")
        library.describer.reply = "A new description"
        library.model.selection = [library.id("a.jpg")]
        library.model.describeFocusedPhotoAgain()
        try await waitUntil("the new description") { library.record(for: "a.jpg")?.text == "A new description" }
        XCTAssertEqual(library.record(for: "a.jpg")?.source, "apple-intelligence")
        XCTAssertEqual(library.record(for: "a.jpg")?.prompt, DescriptionPrompt.standard)
    }

    @MainActor
    func testDescribingAsYouArrowSavesAppleIntelligence() async throws {
        let library = try await makeLibrary()
        library.model.isInFront = { true }
        library.describer.reply = "A grey cat"
        library.model.selection = [library.id("c.jpg")]
        try await waitUntil("the description to be saved") { library.record(for: "c.jpg") != nil }
        await library.model.autoDescriber.whenIdle()
        XCTAssertEqual(library.record(for: "c.jpg")?.source, "apple-intelligence")
        XCTAssertEqual(library.record(for: "c.jpg")?.prompt, DescriptionPrompt.standard)
    }

    @MainActor
    func testTheDescriptionPanelsCorrectionSavesThePersonAndApplesText() async throws {
        let library = try await makeLibrary()
        library.describer.reply = "A dog on sand"
        library.model.selection = [library.id("c.jpg")]
        library.model.describeFocusedPhoto()
        try await waitUntil("the description to be saved") { library.record(for: "c.jpg") != nil }
        library.model.saveEditedDescription("Rex on the beach", for: library.id("c.jpg"))
        let record = try XCTUnwrap(library.record(for: "c.jpg"))
        XCTAssertEqual(record.text, "Rex on the beach")
        XCTAssertEqual(record.source, "person")
        XCTAssertEqual(record.original, "A dog on sand")
        XCTAssertEqual(record.prompt, DescriptionPrompt.standard, "how the corrected description was made is kept")
        XCTAssertNotNil(record.edited)
        XCTAssertEqual(library.said.last?.text, "Description saved", "nothing spoken changes")
    }
}

/// Reads a Descriptions.json the way a test checks it: the version 2 file, its records by key.
enum ProvenanceFile {
    private struct File: Decodable {
        var version: Int
        var descriptions: [String: DescriptionRecord]
    }

    static func records(at url: URL) throws -> [String: DescriptionRecord] {
        let file = try JSONDecoder().decode(File.self, from: Data(contentsOf: url))
        XCTAssertEqual(file.version, DescriptionCache.fileVersion)
        return file.descriptions
    }
}

extension TestLibrary {
    /// The saved record of a file in the test folder.
    func record(for name: String) -> DescriptionRecord? {
        FolderScanner.file(at: path(name)).flatMap { model.descriptions.record(forKey: $0.descriptionKey) }
    }
}
