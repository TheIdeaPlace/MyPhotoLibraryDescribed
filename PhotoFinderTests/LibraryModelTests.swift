import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import Photo_Finder

/// The library model itself (#48), through an added folder of temporary files: no Photos library, no Apple
/// Intelligence (a stand-in describes), and its own settings and description store, so nothing real is touched.
final class LibraryModelTests: XCTestCase {
    // MARK: - Redescribe and describing automatically

    @MainActor
    func testAutomaticDescribingLeavesAPhotoAloneWhileItsRedescribed() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.isInFront = { true }
        library.remember("Old", for: "a.jpg")
        model.selection = [library.id("a.jpg")]
        await model.autoDescriber.whenIdle()
        library.said.removeAll()

        model.describeFocusedPhotoAgain()
        XCTAssertEqual(library.said.last?.text, "Redescribing a.jpg")
        try await waitUntil("Redescribe to ask for a description") { library.describer.waitingCount == 1 }
        XCTAssertEqual(model.descriptionStatus(for: library.id("a.jpg")), "Describing…")
        XCTAssertFalse(model.autoDescriber.isStillOn(library.id("a.jpg")), "automatic describing leaves it alone")

        // Away and back: the old description isn't spoken, and it isn't described a second time.
        model.selection = [library.id("c.jpg")]
        model.selection = [library.id("a.jpg")]
        await model.autoDescriber.whenIdle()
        XCTAssertFalse(library.said.contains { $0.text == "Old" }, "\(library.said)")
        XCTAssertEqual(library.describer.calls, 1)

        // Describe Photo waits for the new description rather than saying the old one.
        model.describeFocusedPhoto()
        XCTAssertEqual(library.said.last?.text, "Redescribing a.jpg")

        library.describer.finish(with: "A new description")
        try await waitUntil("the new description to be spoken") { library.said.contains { $0.text == "A new description" } }
        XCTAssertEqual(model.savedDescription(for: library.id("a.jpg")), "A new description")
        XCTAssertEqual(model.descriptionStatus(for: library.id("a.jpg")), "Saved description")
        XCTAssertTrue(model.autoDescriber.isStillOn(library.id("a.jpg")), "back to normal once Redescribe is done")
    }

    @MainActor
    func testAnAutomaticDescriptionFinishingDuringRedescribeIsNotSaved() async throws {
        let library = try await makeLibrary()
        let model = library.model
        library.remember("Old", for: "a.jpg")
        model.selection = [library.id("a.jpg")]
        model.describeFocusedPhotoAgain()
        try await waitUntil("Redescribe to ask") { library.describer.waitingCount == 1 }

        let automatic = Task { await model.autoDescriber.describe(library.id("a.jpg")) }
        try await waitUntil("the automatic description to ask") { library.describer.waitingCount == 2 }
        library.describer.finish(1, with: "An older automatic description")
        let spoken = await automatic.value
        XCTAssertNil(spoken, "neither saved nor spoken")
        XCTAssertEqual(model.savedDescription(for: library.id("a.jpg")), "Old")

        library.describer.finish(with: "The new description")
        try await waitUntil("Redescribe to save") { model.savedDescription(for: library.id("a.jpg")) == "The new description" }
    }

    @MainActor
    func testANewerRedescribeReplacesAnOlderOneAndTheBookkeepingEvensOut() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.isInFront = { true }
        model.selection = [library.id("a.jpg")]
        model.describeFocusedPhotoAgain()
        try await waitUntil("the first Redescribe to ask") { library.describer.waitingCount == 1 }
        model.describeFocusedPhotoAgain()
        try await waitUntil("the second Redescribe to ask") { library.describer.waitingCount == 2 }

        library.describer.finish(with: "From the first")
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertNil(model.savedDescription(for: library.id("a.jpg")), "the replaced Redescribe saves nothing")
        XCTAssertFalse(model.autoDescriber.isStillOn(library.id("a.jpg")), "the newer one is still running")
        XCTAssertFalse(library.said.contains { $0.text == "From the first" })

        library.describer.finish(with: "From the second")
        try await waitUntil("the second to be spoken") { library.said.contains { $0.text == "From the second" } }
        XCTAssertEqual(model.savedDescription(for: library.id("a.jpg")), "From the second")
        XCTAssertTrue(model.autoDescriber.isStillOn(library.id("a.jpg")))
    }

    @MainActor
    func testDescribePhotoSpeaksASavedDescriptionAndSaysWhatCantBeDescribed() async throws {
        let library = try await makeLibrary()
        let model = library.model
        library.remember("A red door", for: "a.jpg")
        model.selection = [library.id("a.jpg")]
        model.describeFocusedPhoto()
        XCTAssertEqual(library.said.last?.text, "A red door")
        XCTAssertEqual(library.describer.calls, 0)

        model.selection = [library.id("b.mov")]
        model.describeFocusedPhoto()
        XCTAssertEqual(library.said.last?.text, "Videos can't be described")
        model.describeFocusedPhotoAgain()
        XCTAssertEqual(library.said.last?.text, "Videos can't be described")

        model.selection = [library.folderID("d")]
        model.describeFocusedPhoto()
        XCTAssertEqual(library.said.last?.text, "Select a photo to describe")
    }

    @MainActor
    func testDescribePhotoSaysWhyWithoutAppleIntelligence() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.photoDescribing = nil
        model.selection = [library.id("c.jpg")]
        model.describeFocusedPhoto()
        XCTAssertEqual(library.said.last?.text, "Describing photos needs Apple Intelligence on macOS 27 or later.")
        XCTAssertEqual(model.describeUnavailableReason, "Describing photos needs Apple Intelligence on macOS 27 or later.")
    }

    @MainActor
    func testDescribePhotoDescribesAndSpeaksAPhotoNotDescribedYet() async throws {
        let library = try await makeLibrary()
        let model = library.model
        library.describer.reply = "Two cats on a sofa"
        model.selection = [library.id("c.jpg")]
        model.describeFocusedPhoto()
        XCTAssertEqual(library.said.last?.text, "Describing c.jpg")
        try await waitUntil("the description to be spoken") { library.said.last?.text == "Two cats on a sofa" }
        XCTAssertEqual(model.savedDescription(for: library.id("c.jpg")), "Two cats on a sofa")
    }

    // MARK: - descriptionRevision

    @MainActor
    func testSavingADescriptionBumpsTheRevision() async throws {
        let library = try await makeLibrary()
        let model = library.model
        let before = model.descriptionRevision
        library.remember("A lighthouse", for: "a.jpg")
        try await waitUntil("the revision to change") { model.descriptionRevision == before + 1 }
        XCTAssertEqual(model.savedDescription(for: library.id("a.jpg")), "A lighthouse")
        XCTAssertEqual(model.descriptionStatus(for: library.id("a.jpg")), "Saved description")
    }

    @MainActor
    func testEditingADescriptionSavesItAsYoursAndBumpsTheRevision() async throws {
        let library = try await makeLibrary()
        let model = library.model
        let id = library.id("a.jpg")
        XCTAssertEqual(model.descriptionStatus(for: id), "Not described yet")
        let before = model.descriptionRevision

        model.saveEditedDescription("  My own words \n", for: id)
        XCTAssertEqual(library.said.last?.text, "Description saved")
        try await waitUntil("the revision to change") { model.descriptionRevision == before + 1 }
        XCTAssertEqual(model.savedDescription(for: id), "My own words")
        XCTAssertEqual(model.descriptionStatus(for: id), "Edited by you")

        // The same words again change nothing.
        library.said.removeAll()
        model.saveEditedDescription("My own words", for: id)
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(library.said.isEmpty)
        XCTAssertEqual(model.descriptionRevision, before + 1)

        model.saveEditedDescription("   ", for: id)
        XCTAssertEqual(library.said.last?.text, "A description can't be empty. The saved one is kept.")
        XCTAssertEqual(model.savedDescription(for: id), "My own words")
    }

    // MARK: - Home, End and moving with Search

    @MainActor
    func testHomeAndEndGoToTheEndsOfWhatSearchShowsEvenFromAHiddenSelection() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.selection = [library.id("b.mov")]
        model.searchText = "jpg"
        XCTAssertEqual(model.visibleRows.map(\.name), ["a.jpg", "c.jpg"])
        XCTAssertNil(model.focusedRow, "the selection is hidden by Search")

        model.moveSelectionToEnd(last: true)
        XCTAssertEqual(model.selection, [library.id("c.jpg")])
        XCTAssertEqual(library.said.last?.text, "c.jpg, 2 of 2")

        model.selection = [library.id("b.mov")]
        model.moveSelectionToEnd(last: false)
        XCTAssertEqual(model.selection, [library.id("a.jpg")])
        XCTAssertEqual(library.said.last?.text, "a.jpg, 1 of 2")
    }

    @MainActor
    func testMovingFromAHiddenSelectionStartsAtTheTopAndStopsAtTheEnds() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.selection = [library.id("b.mov")]
        model.searchText = "jpg"

        model.moveSelection(by: 1)
        XCTAssertEqual(model.selection, [library.id("a.jpg")])
        XCTAssertEqual(library.said.last?.text, "a.jpg, 1 of 2")
        model.moveSelection(by: 1)
        XCTAssertEqual(library.said.last?.text, "c.jpg, 2 of 2")

        library.said.removeAll()
        model.moveSelection(by: 1)
        XCTAssertEqual(model.selection, [library.id("c.jpg")], "past the end stays put")
        model.moveSelection(by: -1)
        model.moveSelection(by: -1)
        XCTAssertEqual(model.selection, [library.id("a.jpg")])
        XCTAssertEqual(library.said.map(\.text).filter { !$0.hasSuffix("result") && !$0.hasSuffix("results") },
                       ["a.jpg, 1 of 2"])
    }

    // MARK: - Copy Description

    @MainActor
    func testCopyDescriptionCopiesTheTextAndSaysWhatsMissing() async throws {
        let library = try await makeLibrary()
        let model = library.model
        let board = NSPasteboard(name: NSPasteboard.Name("PhotoFinderTests-\(UUID().uuidString)"))
        defer { board.releaseGlobally() }
        model.pasteboard = board

        model.copyDescription()
        XCTAssertEqual(library.said.last?.text, "Select a photo to copy its description")

        model.selection = [library.id("a.jpg")]
        model.copyDescription()
        XCTAssertEqual(library.said.last?.text, "a.jpg hasn't been described yet")

        library.remember("A red door", for: "a.jpg")
        model.copyDescription()
        XCTAssertEqual(board.string(forType: .string), "A red door")
        XCTAssertEqual(library.said.last?.text, "Copied the description")

        library.remember("A blue boat", for: "c.jpg")
        model.copyDescription([library.id("a.jpg"), library.id("c.jpg"), library.id("b.mov")])
        XCTAssertEqual(board.string(forType: .string), "a.jpg\nA red door\n\nc.jpg\nA blue boat")
        XCTAssertEqual(library.said.last?.text, "Copied 2 descriptions", "the video is left out")

        model.copyDescription([library.id("b.mov")])
        XCTAssertEqual(library.said.last?.text, "Videos and folders aren't described")

        // A correction in the Description panel not yet saved is what's copied.
        model.panelDraft = (library.id("a.jpg"), "A red front door")
        model.copyDescription([library.id("a.jpg")])
        XCTAssertEqual(board.string(forType: .string), "A red front door")
    }

    // MARK: - Next Photo and Previous Photo

    @MainActor
    func testNextAndPreviousPhotoSkipFoldersAndVideos() async throws {
        let library = try await makeLibrary()
        let model = library.model
        XCTAssertEqual(model.visibleRows.map(\.name), ["d", "a.jpg", "b.mov", "c.jpg"], "folders first, as on Windows")

        model.stepPhoto(by: 1)
        XCTAssertEqual(model.selection, [library.id("a.jpg")], "with nothing selected, the first photo")
        let scrolls = model.scrollRequest.serial
        model.stepPhoto(by: 1)
        XCTAssertEqual(model.selection, [library.id("c.jpg")], "past the video")
        XCTAssertEqual(library.said.last?.text, "c.jpg, 4 of 4")
        XCTAssertEqual(model.scrollRequest.id, library.id("c.jpg"))
        XCTAssertEqual(model.scrollRequest.serial, scrolls + 1)

        model.stepPhoto(by: 1)
        XCTAssertEqual(model.selection, [library.id("c.jpg")], "the last photo")
        XCTAssertEqual(library.said.last?.text, "No next photo")

        model.stepPhoto(by: -1)
        XCTAssertEqual(model.selection, [library.id("a.jpg")])
        model.stepPhoto(by: -1)
        XCTAssertEqual(model.selection, [library.id("a.jpg")], "not onto the folder")
        XCTAssertEqual(library.said.last?.text, "No previous photo")
    }

    @MainActor
    func testPreviousPhotoWithNothingSelectedStartsFromTheEnd() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.stepPhoto(by: -1)
        XCTAssertEqual(model.selection, [library.id("c.jpg")], "the last photo")
    }

    // MARK: - Sort By

    @MainActor
    func testSortByChoosesTheColumnsFirstDirectionThenReversesIt() async throws {
        let library = try await makeLibrary()
        let model = library.model

        model.sortBy("kind")
        XCTAssertEqual(model.sortColumnID, "kind")
        XCTAssertEqual(library.said.last?.text, "Sorted by Kind, ascending")

        model.sortBy("name")
        XCTAssertEqual(library.said.last?.text, "Sorted by Name, ascending")
        XCTAssertEqual(model.rows.map(\.name), ["d", "a.jpg", "b.mov", "c.jpg"])

        model.sortBy("name")
        XCTAssertEqual(library.said.last?.text, "Sorted by Name, descending")
        XCTAssertEqual(model.rows.map(\.name), ["d", "c.jpg", "b.mov", "a.jpg"])
        model.sortBy("name")
        XCTAssertEqual(library.said.last?.text, "Sorted by Name, ascending")

        model.sortBy("date")
        XCTAssertEqual(library.said.last?.text, "Sorted by Date Taken, descending", "dates start newest first")
        model.sortBy("date")
        XCTAssertEqual(library.said.last?.text, "Sorted by Date Taken, ascending")
    }

    @MainActor
    func testTheSortIsRememberedInTheModelsOwnSettings() async throws {
        let library = try await makeLibrary()
        library.model.sortBy("name")  // already by name, A to Z: reversed
        XCTAssertEqual(library.defaults.string(forKey: ColumnOrder.sortColumnKey), "name")
        XCTAssertEqual(library.defaults.object(forKey: ColumnOrder.sortDescendingKey) as? Bool, true)

        let again = LibraryModel(descriptions: library.model.descriptions, defaults: library.defaults, photoDescribing: nil,
                                 placeNames: library.model.placeNames)
        XCTAssertEqual(again.sortColumnID, "name")
        XCTAssertEqual(again.sortOrder.first?.order, .reverse)
    }

    // MARK: - Read-Only Mode

    @MainActor
    func testCommandsThatChangePhotosAreRefusedWhileReadOnly() async throws {
        let library = try await makeLibrary()
        let model = library.model
        XCTAssertTrue(model.readOnly, "on at every launch")
        XCTAssertTrue(model.windowTitle.hasSuffix("- Read-Only Mode"), model.windowTitle)
        model.selection = [library.id("a.jpg")]

        let commands: [@MainActor () -> Void] = [{ model.useDescriptionAsCaption() }, { model.editCaption() },
                                                 { model.toggleFavorite() }, { model.setRating(3) }]
        for command in commands {
            library.said.removeAll()
            command()
            XCTAssertEqual(library.said.map(\.text), [PhotoChanges.readOnlyMessage])
        }
        XCTAssertNil(model.captionTarget, "no caption sheet")
    }

    @MainActor
    func testSaveInPhotosCantBeTurnedOnWhileReadOnly() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.setSaveInPhotos(true)
        XCTAssertEqual(library.said.last?.text, PhotoChanges.readOnlyMessage)
        XCTAssertFalse(model.saveInPhotos)
        XCTAssertNil(library.defaults.object(forKey: CaptionPolicy.key), "nothing remembered")
        XCTAssertEqual(model.captionPolicy, .never)
    }

    @MainActor
    func testSaveInPhotosIsPausedWhileReadOnlyAndCanStillBeTurnedOff() async throws {
        let library = try await makeLibrary(settings: [CaptionPolicy.key: true])
        let model = library.model
        XCTAssertTrue(model.saveInPhotos)
        XCTAssertFalse(model.saveInPhotosShownOn)
        XCTAssertEqual(model.saveInPhotosTitle, "Save Descriptions in Photos (Paused in Read-Only Mode)")

        // Chosen while paused: turned off, so it can always be turned off.
        model.chooseSaveInPhotos(true)
        XCTAssertFalse(model.saveInPhotos)
        XCTAssertEqual(library.defaults.object(forKey: CaptionPolicy.key) as? Bool, false)
        XCTAssertEqual(library.said.last?.text, "Descriptions aren't saved in your photos")
        XCTAssertEqual(model.saveInPhotosTitle, "Save Descriptions in Photos")
    }

    @MainActor
    func testGoingBackToReadOnlyModeSaysSo() async throws {
        let library = try await makeLibrary()
        library.model.setReadOnly(true)
        XCTAssertTrue(library.model.readOnly)
        XCTAssertFalse(PhotoChanges.allowed)
        XCTAssertEqual(library.said.last?.text, "Read-Only Mode. Photos won't be changed")
    }

    // MARK: - Status bar

    @MainActor
    func testTheStatusBarCountsItemsAndTheSelection() async throws {
        let library = try await makeLibrary()
        let model = library.model
        XCTAssertEqual(model.statusText, "4 items")
        model.selection = [library.id("a.jpg")]
        XCTAssertEqual(model.statusText, "1 of 4 items selected")
        model.selection = [library.id("a.jpg"), library.id("c.jpg")]
        XCTAssertEqual(model.statusText, "2 of 4 items selected")
        model.selection = []
        model.searchText = "mov"
        XCTAssertEqual(model.statusText, "1 item")
    }

    @MainActor
    func testReadStatusBarSaysEverySection() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.readStatusBar()
        XCTAssertEqual(library.said.last?.text, "4 items")
        model.backgroundProblem = "Background describing stopped"
        model.readStatusBar()
        XCTAssertEqual(library.said.last?.text, "4 items. Background describing stopped")
        model.backgroundProgress = "Describing 3 of 10"
        model.readStatusBar()
        XCTAssertEqual(library.said.last?.text, "4 items. Describing 3 of 10", "what it's doing, before why it stopped")
    }

    // MARK: - Search

    @MainActor
    func testSearchFiltersRowsByNameAndKindIgnoringCase() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.searchText = "C.JPG"
        XCTAssertEqual(model.visibleRows.map(\.name), ["c.jpg"])
        model.searchText = "video"
        XCTAssertEqual(model.visibleRows.map(\.name), ["b.mov"], "by kind")
        model.searchText = "folder"
        XCTAssertEqual(model.visibleRows.map(\.name), ["d"], "a folder's kind")
        model.searchText = "   "
        XCTAssertEqual(model.visibleRows.count, 4, "spaces alone don't filter")
        model.searchText = "nothing like this"
        XCTAssertTrue(model.visibleRows.isEmpty)
        XCTAssertEqual(model.rows.count, 4, "the rows themselves are kept")
    }

    @MainActor
    func testSearchSaysHowManyResultsOnceTypingPauses() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.searchText = "j"
        model.searchText = "jpg"
        try await waitUntil("the result count") { library.said.contains { $0.text == "2 results" } }
        XCTAssertEqual(library.said.map(\.text), ["2 results"], "said once, for the last search")
        XCTAssertEqual(library.said.last?.queued, true, "after what VoiceOver is saying")
    }
}

// MARK: - Test library

/// Apple Intelligence's stand-in: counts requests, and answers each one when told to (or at once with `reply`).
@MainActor
final class StandInDescriber {
    private(set) var calls = 0
    /// The prompt each request was asked with.
    private(set) var prompts: [String] = []
    var reply: String?
    private var waiting: [CheckedContinuation<String?, Never>] = []
    var waitingCount: Int { waiting.count }

    var describing: PhotoDescribing {
        PhotoDescribing(unavailableReason: { nil }, describe: { [self] _, prompt in prompts.append(prompt); return await answer() },
                        failureMessage: { "Failed: \($0.localizedDescription)" })
    }

    private func answer() async -> String? {
        calls += 1
        if let reply { return reply }
        return await withCheckedContinuation { waiting.append($0) }
    }

    func finish(_ index: Int = 0, with text: String) {
        waiting.remove(at: index).resume(returning: text)
    }
}

/// A library model showing an added folder of temporary files, with settings and descriptions of its own:
/// a.jpg, b.mov, c.jpg and a subfolder d holding e.jpg, sorted by name.
@MainActor
final class TestLibrary {
    let model: LibraryModel
    let defaults: UserDefaults
    let root: String
    let describer = StandInDescriber()
    var said: [(text: String, queued: Bool)] = []

    /// `geocodePlace` stands in for Apple's place names (#172); without one, asking fails the test.
    init(root: String, defaults: UserDefaults, cacheURL: URL, geocodePlace: PlaceGeocoder? = nil) {
        self.root = root
        self.defaults = defaults
        let placeNames = PlaceNameCache(fileURL: cacheURL.deletingLastPathComponent().appendingPathComponent("PlaceNames.json"))
        model = LibraryModel(descriptions: DescriptionCache(fileURL: cacheURL), defaults: defaults,
                             photoDescribing: describer.describing, placeNames: placeNames,
                             geocodePlace: geocodePlace ?? { _, _ in XCTFail("Apple was asked for a place name"); return nil })
        // Tests run without the window in front; those about describing automatically say it is.
        model.isInFront = { false }
        model.announcer = { [weak self] text, queued in self?.said.append((text, queued)) }
    }

    func path(_ name: String) -> String { (root as NSString).appendingPathComponent(name) }
    func id(_ name: String) -> String { FolderID.file(path(name)) }
    func folderID(_ name: String) -> String { FolderID.folder(path(name)) }

    /// Saves a description of a file, as Apple Intelligence would.
    func remember(_ text: String, for name: String) {
        guard let file = FolderScanner.file(at: path(name)) else { return XCTFail("no file \(name)") }
        model.descriptions.remember(text, forKey: file.descriptionKey)
    }
}

struct TimedOut: Error {}

extension XCTestCase {
    @MainActor
    func waitUntil(_ what: String, timeout: TimeInterval = 5, _ condition: @escaping @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            guard Date() < deadline else {
                XCTFail("Timed out waiting for \(what)")
                throw TimedOut()
            }
            try await Task.sleep(for: .milliseconds(10))
        }
    }

    /// Makes the test library, shows its folder, and waits until every file's details are read. The files named in
    /// `onlyInCloud` are treated as kept only in the cloud (#197); nothing real is in the cloud. `withGIF` adds f.gif,
    /// a photo whose format can't hold a caption.
    @MainActor
    func makeLibrary(settings: [String: Any] = [:], onlyInCloud: Set<String> = [],
                     withGIF: Bool = false) async throws -> TestLibrary {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("LibraryModelTests-\(UUID().uuidString)")
        let photos = base.appendingPathComponent("Photos", isDirectory: true)
        let subfolder = photos.appendingPathComponent("d", isDirectory: true)
        try FileManager.default.createDirectory(at: subfolder, withIntermediateDirectories: true)
        for url in [photos.appendingPathComponent("a.jpg"), photos.appendingPathComponent("c.jpg"),
                    subfolder.appendingPathComponent("e.jpg")] {
            try writeJPEG(to: url)
        }
        try Data("not really a movie".utf8).write(to: photos.appendingPathComponent("b.mov"))
        if withGIF { try writeJPEG(to: photos.appendingPathComponent("f.gif"), type: .gif) }

        // One fixed suite, cleared first and after: a new name each time left an empty file in ~/Library/Preferences
        // per test (#143's review).
        let suite = "LibraryModelTests"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defaults.removePersistentDomain(forName: suite)
        defaults.set("name", forKey: ColumnOrder.sortColumnKey)
        defaults.set(false, forKey: ColumnOrder.sortDescendingKey)
        for (key, value) in settings { defaults.set(value, forKey: key) }
        addTeardownBlock {
            UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite)
            try? FileManager.default.removeItem(at: base)
        }

        let root = FolderRoots.normalize(photos.path)
        let library = TestLibrary(root: root, defaults: defaults,
                                  cacheURL: base.appendingPathComponent("Store").appendingPathComponent("Descriptions.json"))
        let model = library.model
        model.isOnThisMac = { !onlyInCloud.contains($0.lastPathComponent) }
        model.addFolders([root])
        try await waitUntil("the folder in the sidebar") { model.itemsByID[FolderID.folder(root)] != nil }
        model.selectedSourceID = FolderID.folder(root)
        try await waitUntil("the folder's details") {
            !model.isLoading && model.rows.count == (withGIF ? 5 : 4)
                && model.rows.filter { $0.name.hasSuffix(".jpg") && !onlyInCloud.contains($0.name) }
                    .allSatisfy { !$0.dimensions.isEmpty }
        }
        library.said.removeAll()
        return library
    }

    private func writeJPEG(to url: URL, type: UTType = .jpeg) throws {
        let context = try XCTUnwrap(CGContext(data: nil, width: 8, height: 6, bitsPerComponent: 8, bytesPerRow: 0,
                                              space: CGColorSpaceCreateDeviceRGB(),
                                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        let destination = try XCTUnwrap(CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, try XCTUnwrap(context.makeImage()), nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
    }
}
