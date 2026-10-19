import XCTest
@testable import Photo_Finder

/// The description store on disk (#21, #199): Descriptions.json, reading the older flat format Windows' DescriptionStore
/// still writes, and keeping a description when the app writes into its file. Each test has a temporary folder of
/// its own; the real Application Support folder is never touched.
final class DescriptionStoreTests: XCTestCase {
    /// A new folder for one test's store, removed afterwards. A description saved with `remember` is written again a
    /// couple of seconds later, so the folder is removed once more after that.
    private func makeFolder() -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("DescriptionStoreTests-\(UUID().uuidString)")
        addTeardownBlock {
            try? FileManager.default.removeItem(at: folder)
            DispatchQueue.global().asyncAfter(deadline: .now() + 3) { try? FileManager.default.removeItem(at: folder) }
        }
        return folder
    }

    private func descriptionsURL(in folder: URL) -> URL { folder.appendingPathComponent("Descriptions.json") }

    private func write(_ text: String, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
    }

    // MARK: - Reading what's on disk

    func testANewStoreWithNoFileIsEmpty() {
        let cache = DescriptionCache(fileURL: descriptionsURL(in: makeFolder()))
        XCTAssertEqual(cache.count, 0)
        XCTAssertNil(cache.description(forKey: "photo"))
        XCTAssertFalse(cache.isEdited(forKey: "photo"))
    }

    func testADamagedFileStartsEmptyAndIsReplacedOnTheNextSave() throws {
        let folder = makeFolder()
        try write("{ not json", to: descriptionsURL(in: folder))
        let cache = DescriptionCache(fileURL: descriptionsURL(in: folder))
        XCTAssertEqual(cache.count, 0)
        // Kept beside it first (#201's review), so nothing is lost by writing over it.
        let aside = try FileManager.default.contentsOfDirectory(atPath: folder.path)
            .filter { $0.hasPrefix("Descriptions.unreadable-") && $0.hasSuffix(".json") }
        XCTAssertEqual(aside.count, 1, "\(aside)")
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent(aside[0]), encoding: .utf8), "{ not json")
        cache.remember("A cat.", forKey: "photo")
        cache.save()
        XCTAssertEqual(try savedDescriptions(in: folder), ["photo": "A cat."])
    }

    func testAnUnreadableFileIsKeptUnderADatedNameThatDoesntReplaceAnother() throws {
        let folder = makeFolder()
        let url = descriptionsURL(in: folder)
        var parts = DateComponents()
        (parts.year, parts.month, parts.day, parts.hour, parts.minute, parts.second) = (2026, 10, 14, 18, 30, 5)
        let date = try XCTUnwrap(Calendar.current.date(from: parts))
        let aside = DescriptionCache.asideURL(for: url, at: date)
        XCTAssertEqual(aside.lastPathComponent, "Descriptions.unreadable-20261014-183005.json")
        XCTAssertEqual(aside.deletingLastPathComponent().path, folder.path)
        try write("{}", to: aside)
        XCTAssertEqual(DescriptionCache.asideURL(for: url, at: date).lastPathComponent,
                       "Descriptions.unreadable-20261014-183005-2.json")
    }

    func testAnEmptyFileIsntKeptAside() throws {
        let folder = makeFolder()
        try write("", to: descriptionsURL(in: folder))
        let cache = DescriptionCache(fileURL: descriptionsURL(in: folder))
        XCTAssertEqual(cache.count, 0)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: folder.path), ["Descriptions.json"])
    }

    func testAWriteOnItsWayNeverPutsBackAnOlderDescription() async throws {
        let folder = makeFolder()
        let cache = DescriptionCache(fileURL: descriptionsURL(in: folder))
        cache.remember("One", forKey: "photo")  // schedules a write in two seconds
        cache.save()
        cache.remember("Two", forKey: "photo")
        cache.save()
        try await Task.sleep(for: .milliseconds(2600))
        cache.save()  // after the scheduled write
        XCTAssertEqual(try savedDescriptions(in: folder), ["photo": "Two"])
    }

    // MARK: - Saving

    func testSavingMakesTheFolderAndMarksTheCorrectionsInTheirRecords() throws {
        let folder = makeFolder().appendingPathComponent("Not There Yet", isDirectory: true)
        let cache = DescriptionCache(fileURL: descriptionsURL(in: folder))
        cache.remember("Zebra corrected", forKey: "z", edited: true)
        cache.remember("Apple corrected", forKey: "a", edited: true)
        cache.remember("Made by Apple Intelligence", forKey: "m")
        cache.save()
        XCTAssertEqual(try savedDescriptions(in: folder),
                       ["z": "Zebra corrected", "a": "Apple corrected", "m": "Made by Apple Intelligence"])
        let records = try savedRecords(in: folder)
        XCTAssertEqual(records["a"]?.source, "person")
        XCTAssertEqual(records["z"]?.source, "person")
        XCTAssertNotEqual(records["m"]?.source, "person")
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("EditedDescriptions.json").path), "no separate file of marks")
    }

    func testAClearedMarkStaysClearedAfterARestart() throws {
        let folder = makeFolder()
        let cache = DescriptionCache(fileURL: descriptionsURL(in: folder))
        cache.remember("Mine", forKey: "photo", edited: true)
        cache.save()
        cache.remember("Redescribed", forKey: "photo")
        cache.save()
        XCTAssertFalse(DescriptionCache(fileURL: descriptionsURL(in: folder)).isEdited(forKey: "photo"))
    }

    func testADescriptionIsSavedByItselfAfterAPause() async throws {
        let folder = makeFolder()
        let cache = DescriptionCache(fileURL: descriptionsURL(in: folder))
        cache.remember("A cat.", forKey: "photo")
        XCTAssertFalse(FileManager.default.fileExists(atPath: descriptionsURL(in: folder).path),
                       "not written after every description")
        let deadline = Date().addingTimeInterval(6)
        while !FileManager.default.fileExists(atPath: descriptionsURL(in: folder).path), Date() < deadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(DescriptionCache(fileURL: descriptionsURL(in: folder)).description(forKey: "photo"), "A cat.")
    }

    func testUnicodeAndQuotesSurviveARestart() throws {
        let folder = makeFolder()
        let text = "Ein \"Schild\" mit „OPEN“ – café, 日本語, emoji 🐈, a back\\slash and a / slash"
        let key = "file:/USERS/K/FOTOS/ÜBER.JPG|12|638000000000000000"
        let cache = DescriptionCache(fileURL: descriptionsURL(in: folder))
        cache.remember(text, forKey: key)
        cache.save()
        XCTAssertEqual(DescriptionCache(fileURL: descriptionsURL(in: folder)).description(forKey: key), text)
        XCTAssertEqual(try savedDescriptions(in: folder), [key: text])
    }

    func testInnerLinesAreKeptAndOnlyTheEndsTrimmed() {
        let cache = DescriptionCache(fileURL: descriptionsURL(in: makeFolder()))
        cache.remember("\n  A cat.\n\nOn a sofa.  \n", forKey: "photo")
        XCTAssertEqual(cache.description(forKey: "photo"), "A cat.\n\nOn a sofa.")
    }

    func testCountIsOnePerPhotoNotPerSave() {
        let cache = DescriptionCache(fileURL: descriptionsURL(in: makeFolder()))
        cache.remember("One", forKey: "a")
        cache.remember("Two", forKey: "a")
        cache.remember("Three", forKey: "b")
        cache.remember("  ", forKey: "c")
        XCTAssertEqual(cache.count, 2)
    }

    func testAnEmptyCorrectionNeitherSavesNorMarks() {
        let cache = DescriptionCache(fileURL: descriptionsURL(in: makeFolder()))
        cache.remember("A dog", forKey: "photo")
        cache.remember(" \n ", forKey: "photo", edited: true)
        XCTAssertEqual(cache.description(forKey: "photo"), "A dog")
        XCTAssertFalse(cache.isEdited(forKey: "photo"))
    }

    func testTheScreenIsToldWhichPhotoAndWhatItsNewDescriptionIs() {
        let cache = DescriptionCache(fileURL: descriptionsURL(in: makeFolder()))
        let changed = expectation(forNotification: DescriptionCache.didChange, object: cache) { note in
            note.userInfo?[DescriptionCache.keyInfo] as? String == "photo"
                && note.userInfo?[DescriptionCache.textInfo] as? String == "A dog"
        }
        cache.remember("  A dog \n", forKey: "photo")
        wait(for: [changed], timeout: 2)
    }

    func testTheStoreLivesInTheAppsDataFolder() {
        let url = DescriptionCache.defaultURL
        XCTAssertEqual(url.lastPathComponent, "Descriptions.json")
        XCTAssertEqual(url.deletingLastPathComponent().lastPathComponent, AppInfo.dataFolderName)
        XCTAssertEqual(url.deletingLastPathComponent().deletingLastPathComponent().lastPathComponent, "Application Support")
    }

    // MARK: - Rekeying when the app writes into a file

    func testRekeyMovesTheDescriptionAndItsEditedMark() throws {
        let folder = makeFolder()
        let cache = DescriptionCache(fileURL: descriptionsURL(in: folder))
        cache.remember("Mine", forKey: "old", edited: true)
        cache.remember("Other", forKey: "other")
        cache.rekey(from: "old", to: "new")
        XCTAssertNil(cache.description(forKey: "old"))
        XCTAssertFalse(cache.isEdited(forKey: "old"))
        XCTAssertEqual(cache.description(forKey: "new"), "Mine")
        XCTAssertTrue(cache.isEdited(forKey: "new"))
        XCTAssertEqual(cache.count, 2)
        cache.save()
        XCTAssertEqual(try savedDescriptions(in: folder), ["new": "Mine", "other": "Other"])
    }

    func testRekeyOfAnUndescribedFileOrToTheSameKeyChangesNothing() {
        let cache = DescriptionCache(fileURL: descriptionsURL(in: makeFolder()))
        cache.remember("A dog", forKey: "photo")
        cache.rekey(from: "nothing here", to: "photo")
        XCTAssertEqual(cache.description(forKey: "photo"), "A dog", "a missing key doesn't clear the new one")
        cache.rekey(from: "photo", to: "photo")
        XCTAssertEqual(cache.description(forKey: "photo"), "A dog")
        XCTAssertEqual(cache.count, 1)
    }

    // MARK: - Helpers

    private func savedDescriptions(in folder: URL) throws -> [String: String] {
        try savedRecords(in: folder).mapValues { $0.text }
    }

    private func savedRecords(in folder: URL) throws -> [String: DescriptionRecord] {
        try ProvenanceFile.records(at: descriptionsURL(in: folder))
    }
}

/// What Apple Intelligence is asked (#21). The setting itself is read from the app's defaults, so only `resolve`
/// is tested, never the real preference.
final class DescriptionPromptMoreTests: XCTestCase {
    func testTheStandardPromptAsksForASummaryThenTheDetailsAndAnyText() {
        let prompt = DescriptionPrompt.standard
        XCTAssertTrue(prompt.hasPrefix("Describe this photo."), prompt)
        XCTAssertTrue(prompt.contains("one-sentence summary"), prompt)
        XCTAssertTrue(prompt.contains("any text that appears in the photo"), prompt)
        XCTAssertFalse(prompt.contains("\n"), "one paragraph: the line breaks in the source are joined")
        XCTAssertFalse(prompt.contains("  "), "no doubled spaces where the lines are joined")
    }

    func testYourOwnPromptIsKeptWholeInsideButTrimmedAtTheEnds() {
        XCTAssertEqual(DescriptionPrompt.resolve("\tList the people.\nThen the place.  "), "List the people.\nThen the place.")
        XCTAssertEqual(DescriptionPrompt.resolve(""), DescriptionPrompt.standard)
        XCTAssertEqual(DescriptionPrompt.resolve("\t"), DescriptionPrompt.standard)
    }

    func testTheSettingsKeyIsStable() {
        // Settings saves the prompt under this name; changing it would lose the prompts people have written.
        XCTAssertEqual(DescriptionPrompt.key, "describePrompt")
    }
}
