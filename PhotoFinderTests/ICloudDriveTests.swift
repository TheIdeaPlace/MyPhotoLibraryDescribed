import XCTest
@testable import Photo_Finder

/// Files in an added folder kept only in iCloud Drive (#197): the list doesn't read them, describing as you arrow and
/// ahead leaves them, and writing into them asks first, as Windows does for OneDrive's online-only files. iCloud is
/// stood in for: the files are temporary ones on this Mac that the model is told are only in iCloud Drive.
final class ICloudDriveTests: XCTestCase {
    override func tearDown() {
        PhotoChanges.allowed = false
        super.tearDown()
    }

    /// The questions asked, answered with `answer`.
    @MainActor
    private final class Questions {
        var asked: [String] = []
        var answer = false
    }

    @MainActor
    private func allowChanges(_ library: TestLibrary) -> Questions {
        let questions = Questions()
        library.model.confirmAllowChanges = { true }
        library.model.setReadOnly(false)
        library.model.confirmDownload = { _, question in
            questions.asked.append(question)
            return questions.answer
        }
        library.said.removeAll()
        return questions
    }

    @MainActor
    private func caption(_ library: TestLibrary, _ name: String) -> String {
        FileMetadata.read(URL(fileURLWithPath: library.path(name))).caption
    }

    // MARK: - The question

    func testTheQuestionBeforeWritingSaysHowManyWouldBeDownloaded() {
        XCTAssertNil(FileAvailability.writeQuestion(action: "Edit Caption", files: 3, onlyInICloud: 0), "nothing to ask")
        XCTAssertEqual(FileAvailability.writeQuestion(action: "Edit Caption", files: 1, onlyInICloud: 1),
                       "Edit Caption changes 1 file on your Mac. It's only in iCloud Drive, and will be downloaded to change it. Continue?")
        XCTAssertEqual(FileAvailability.writeQuestion(action: "Use Description as Caption", files: 3, onlyInICloud: 2),
                       "Use Description as Caption changes 3 files on your Mac. 2 of them are only in iCloud Drive, and each will be downloaded to change it. Continue?")
        XCTAssertEqual(FileAvailability.writeQuestion(action: "Edit Caption", files: 3, onlyInICloud: 1),
                       "Edit Caption changes 3 files on your Mac. 1 of them is only in iCloud Drive, and will be downloaded to change it. Continue?")
        XCTAssertEqual(FileAvailability.writeQuestion(action: "Edit Caption", files: 2, onlyInICloud: 2),
                       "Edit Caption changes 2 files on your Mac. All of them are only in iCloud Drive, and each will be downloaded to change it. Continue?")
    }

    func testAFileOutsideICloudDriveIsOnThisMac() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("ICloudDriveTests-\(UUID().uuidString).jpg")
        try Data("x".utf8).write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }
        XCTAssertTrue(FileAvailability.isOnThisMac(url))
    }

    // MARK: - The list

    @MainActor
    func testTheListShowsWhatTheFileSystemSaysWithoutReadingAFileOnlyInICloudDrive() async throws {
        let library = try await makeLibrary(onlyInICloud: ["c.jpg"])
        let model = library.model
        let a = try XCTUnwrap(model.rows.first { $0.name == "a.jpg" })
        let c = try XCTUnwrap(model.rows.first { $0.name == "c.jpg" })
        XCTAssertEqual(a.dimensions, "8 × 6", "read: it's on this Mac")
        XCTAssertEqual(c.dimensions, "", "not read: reading it would download it")
        let file = try XCTUnwrap(FolderScanner.file(at: library.path("c.jpg")))
        XCTAssertEqual(c.date, file.created ?? file.modified, "Date Taken falls back to when the file was made")
        XCTAssertEqual(c.modified, file.modified)
        XCTAssertNil(model.nameProgress)
    }

    // MARK: - Describing

    @MainActor
    func testDescribingAsYouArrowAndAheadLeaveAFileOnlyInICloudDrive() async throws {
        let library = try await makeLibrary(onlyInICloud: ["c.jpg"])
        let model = library.model
        model.isInFront = { true }
        library.describer.reply = "A grey cat"
        model.selection = [library.id("c.jpg")]
        await model.autoDescriber.whenIdle()
        XCTAssertEqual(library.describer.calls, 0, "not downloaded to describe it")
        XCTAssertNil(model.savedDescription(for: library.id("c.jpg")))
        XCTAssertTrue(library.said.isEmpty, "\(library.said)")

        await model.autoDescriber.describeAhead(library.id("c.jpg"))
        XCTAssertEqual(library.describer.calls, 0, "not described ahead either")

        // A saved description needs nothing from the file, so it's still spoken.
        library.remember("A saved description", for: "c.jpg")
        model.selection = [library.id("a.jpg")]
        await model.autoDescriber.whenIdle()
        model.selection = [library.id("c.jpg")]
        await model.autoDescriber.whenIdle()
        XCTAssertEqual(library.said.last?.text, "A saved description")
    }

    @MainActor
    func testDescribePhotoStillDescribesAFileOnlyInICloudDriveWhenAsked() async throws {
        let library = try await makeLibrary(onlyInICloud: ["c.jpg"])
        let model = library.model
        library.describer.reply = "A grey cat"
        model.selection = [library.id("c.jpg")]
        model.describeFocusedPhoto()
        try await waitUntil("the description to be spoken") { library.said.last?.text == "A grey cat" }
        XCTAssertEqual(model.savedDescription(for: library.id("c.jpg")), "A grey cat")
    }

    // MARK: - Writing into files

    @MainActor
    func testEditCaptionAsksBeforeDownloadingAFileOnlyInICloudDrive() async throws {
        let library = try await makeLibrary(onlyInICloud: ["c.jpg"])
        let model = library.model
        let questions = allowChanges(library)
        model.selection = [library.id("a.jpg"), library.id("c.jpg")]
        model.editCaption()
        try await waitUntil("the question") { !questions.asked.isEmpty }
        XCTAssertEqual(questions.asked, ["Edit Caption changes 2 files on your Mac. 1 of them is only in iCloud Drive, and will be downloaded to change it. Continue?"])
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertNil(model.captionTarget, "cancelled: no caption sheet")

        questions.answer = true
        model.editCaption()
        try await waitUntil("the caption sheet") { model.captionTarget != nil }
        XCTAssertEqual(questions.asked.count, 2)
        model.captionTarget = nil

        // Only files on this Mac: nothing's asked.
        model.selection = [library.id("a.jpg")]
        model.editCaption()
        try await waitUntil("the caption sheet") { model.captionTarget != nil }
        XCTAssertEqual(questions.asked.count, 2)
    }

    @MainActor
    func testUseDescriptionAsCaptionAsksBeforeDownloadingAndCancelLeavesTheFiles() async throws {
        let library = try await makeLibrary(onlyInICloud: ["c.jpg"])
        let model = library.model
        library.remember("A red door", for: "a.jpg")
        library.remember("A grey cat", for: "c.jpg")
        let questions = allowChanges(library)
        model.selection = [library.id("a.jpg"), library.id("c.jpg")]
        model.useDescriptionAsCaption()
        try await waitUntil("the question") { !questions.asked.isEmpty }
        XCTAssertEqual(questions.asked, ["Use Description as Caption changes 2 files on your Mac. 1 of them is only in iCloud Drive, and will be downloaded to change it. Continue?"])
        try await Task.sleep(for: .milliseconds(200))
        XCTAssertEqual(caption(library, "a.jpg"), "", "cancelled: nothing written")
        XCTAssertEqual(caption(library, "c.jpg"), "")

        questions.answer = true
        model.useDescriptionAsCaption()
        try await waitUntil("the captions") { library.said.contains { $0.text == "Captions saved for 2 photos" } }
        XCTAssertEqual(caption(library, "a.jpg"), "A red door")
        XCTAssertEqual(caption(library, "c.jpg"), "A grey cat")
    }

    @MainActor
    func testSaveDescriptionsInPhotosLeavesAFileOnlyInICloudDrive() async throws {
        let library = try await makeLibrary(settings: [CaptionPolicy.key: true], onlyInICloud: ["c.jpg"])
        let model = library.model
        let questions = allowChanges(library)
        library.describer.reply = "A grey cat"
        model.selection = [library.id("c.jpg")]
        model.describeFocusedPhoto()
        try await waitUntil("c.jpg's description") { model.savedDescription(for: library.id("c.jpg")) == "A grey cat" }

        // a.jpg, on this Mac, gets its caption: by then c.jpg would have had its, had it been written.
        library.describer.reply = "A red door"
        model.selection = [library.id("a.jpg")]
        model.describeFocusedPhoto()
        try await waitUntil("a.jpg's caption") { self.caption(library, "a.jpg") == "A red door" }
        XCTAssertEqual(caption(library, "c.jpg"), "", "only in iCloud Drive: left as it is, without asking")
        XCTAssertTrue(questions.asked.isEmpty)
    }
}
