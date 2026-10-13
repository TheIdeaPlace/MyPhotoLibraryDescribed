import Photos
import XCTest
@testable import Photo_Finder

/// Building the sidebar (#21), as Windows' SidebarBuilderTests cover it: the Dates section from photo counts, the
/// order of Photos' smart albums, and the Folders section from added folders. Nothing is read from Photos.
final class SidebarTests: XCTestCase {
    func testDatesAreYearsNewestFirstWithTheirMonthsNewestFirst() throws {
        let items = LibraryModel.dateItems([2023: [12: 1], 2024: [3: 2, 7: 2], 2019: [1: 5]])
        XCTAssertEqual(items.map(\.title), ["2024", "2023", "2019"])
        let year = try XCTUnwrap(items.first)
        XCTAssertEqual(year.id, "year:2024")
        XCTAssertEqual(year.kind, .year(2024))
        XCTAssertEqual(year.count, 4, "every month's photos")
        XCTAssertEqual(year.containerKind, "Year")
        XCTAssertNil(year.parentID)
        let months = try XCTUnwrap(year.children)
        XCTAssertEqual(months.map(\.id), ["month:2024-7", "month:2024-3"])
        XCTAssertEqual(months.map(\.kind), [.month(year: 2024, month: 7), .month(year: 2024, month: 3)])
        XCTAssertEqual(months.map(\.count), [2, 2])
        XCTAssertEqual(months.map(\.parentID), ["year:2024", "year:2024"])
        XCTAssertEqual(months.map(\.containerKind), ["Month", "Month"])
        XCTAssertTrue(months.allSatisfy { $0.children == nil }, "a month has no disclosure triangle")
    }

    func testNoPhotosWithDatesMeansNoYears() {
        XCTAssertEqual(LibraryModel.dateItems([:]), [])
    }

    func testAMonthIsNamedInFullWithItsYear() throws {
        let july = try XCTUnwrap(DateComponents(calendar: .current, year: 2024, month: 7, day: 1).date)
        XCTAssertEqual(LibraryModel.monthTitle(year: 2024, month: 7), july.formatted(.dateTime.month(.wide).year()))
        XCTAssertEqual(LibraryModel.dateItems([2024: [7: 1]]).first?.children?.first?.title,
                       LibraryModel.monthTitle(year: 2024, month: 7))
    }

    func testAMonthRowListsUnderItsYear() throws {
        // Opening a year lists its months as rows, which read as "Month, 2 items".
        let year = try XCTUnwrap(LibraryModel.dateItems([2024: [3: 2]]).first)
        let rows = try XCTUnwrap(year.children).map { FileRow(container: $0) }
        XCTAssertEqual(rows.map(\.kind), ["Month, 2 items"])
        XCTAssertEqual(FileRow(container: year).kind, "Year, 2 items")
    }

    @MainActor
    func testSmartAlbumsStartWithFavoritesAndRecentlyAddedAndHideTheLibraryAndDeleted() {
        XCTAssertEqual(Array(LibraryModel.smartAlbumOrder.prefix(3)),
                       [.smartAlbumFavorites, .smartAlbumRecentlyAdded, .smartAlbumVideos], "as on Windows")
        XCTAssertTrue(LibraryModel.hiddenSmartAlbums.contains(PHAssetCollectionSubtype.smartAlbumUserLibrary.rawValue),
                      "All Photos stands in for the library")
        XCTAssertTrue(LibraryModel.hiddenSmartAlbums.contains(PHAssetCollectionSubtype.smartAlbumAllHidden.rawValue))
        XCTAssertTrue(LibraryModel.hiddenSmartAlbums.contains(1_000_000_201), "Recently Deleted")
        XCTAssertFalse(LibraryModel.hiddenSmartAlbums.contains(PHAssetCollectionSubtype.smartAlbumFavorites.rawValue))
        XCTAssertEqual(Set(LibraryModel.smartAlbumOrder).count, LibraryModel.smartAlbumOrder.count, "each album once")
    }

    @MainActor
    func testSmartAlbumsHaveTheirOwnSymbols() {
        XCTAssertEqual(LibraryModel.symbol(for: .smartAlbumFavorites), "heart")
        XCTAssertEqual(LibraryModel.symbol(for: .smartAlbumRecentlyAdded), "clock")
        XCTAssertEqual(LibraryModel.symbol(for: .smartAlbumCinematic), "video")
        XCTAssertEqual(LibraryModel.symbol(for: .smartAlbumGeneric), "rectangle.stack")
    }

    func testAFolderItemKnowsItsPathAndOthersDont() {
        let folder = SidebarItem(id: FolderID.folder("/p"), title: "p", kind: .directory("/p"), systemImage: "folder",
                                 count: 0, children: nil, parentID: nil, containerKind: "Folder")
        XCTAssertEqual(folder.directoryPath, "/p")
        XCTAssertTrue(folder.isDirectory)
        let year = LibraryModel.dateItems([2024: [1: 1]])[0]
        XCTAssertNil(year.directoryPath)
        XCTAssertFalse(year.isDirectory)
    }

    // MARK: - The Folders section, through the library model

    @MainActor
    func testAnAddedFolderIsATreeUnderFoldersWithEveryItemFound() async throws {
        let library = try await makeLibrary()
        let model = library.model
        // Tests have no Photos access and never ask, so Folders is the only section.
        XCTAssertEqual(model.sections.map(\.title), ["Folders"])
        let root = try XCTUnwrap(model.sections.first?.items.first)
        XCTAssertEqual(root.id, FolderID.folder(library.root))
        XCTAssertEqual(root.title, "Photos")
        XCTAssertEqual(root.count, 4, "a.jpg, b.mov, c.jpg and d's e.jpg")
        XCTAssertEqual(root.children?.map(\.title), ["d"])
        XCTAssertEqual(root.children?.first?.count, 1)
        XCTAssertEqual(model.itemsByID[library.folderID("d")]?.parentID, root.id, "subfolders are found by id too")
        XCTAssertEqual(model.rows.first { $0.name == "d" }?.kind, "Folder, 1 item")
        XCTAssertEqual(model.currentTitle, "Photos")
    }

    @MainActor
    func testOpeningASubfolderListsItsPhotos() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.selectedSourceID = library.folderID("d")
        try await waitUntil("the subfolder's photos") { !model.isLoading && model.rows.map(\.name) == ["e.jpg"] }
        XCTAssertEqual(model.currentTitle, "d")
        XCTAssertEqual(model.statusText, "1 item")
    }

    @MainActor
    func testAddingAFolderAlreadyThereSaysSoAndChangesNothing() async throws {
        let library = try await makeLibrary()
        let model = library.model
        let before = model.sections
        model.addFolders([library.root])
        XCTAssertEqual(library.said.last?.text, "Photos is already in the library")
        model.addFolders([library.path("d")])
        XCTAssertEqual(library.said.last?.text, "d is already in the library", "a folder inside one is already listed")
        XCTAssertEqual(model.folderRoots, [library.root])
        try await Task.sleep(for: .milliseconds(200))
        XCTAssertEqual(model.sections, before)
    }

    @MainActor
    func testAddingTwoFoldersSaysHowManyAndListsBoth() async throws {
        let library = try await makeLibrary()
        let model = library.model
        let others = FileManager.default.temporaryDirectory.appendingPathComponent("SidebarTests-\(UUID().uuidString)")
        addTeardownBlock { try? FileManager.default.removeItem(at: others) }
        let first = others.appendingPathComponent("Holiday"), second = others.appendingPathComponent("Work")
        for folder in [first, second] {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try Data().write(to: folder.appendingPathComponent("x.png"))
        }
        model.addFolders([first.path, second.path])
        XCTAssertEqual(library.said.last?.text, "Added 2 folders")
        try await waitUntil("both folders in the sidebar") { model.sections.first?.items.count == 3 }
        XCTAssertEqual(model.sections.first?.items.map(\.title), ["Photos", "Holiday", "Work"], "in the order added")
        XCTAssertEqual(FolderRoots.load(from: library.defaults).count, 3, "remembered in the model's own settings")
    }
}

/// The status bar (#21), alongside LibraryModelTests' status bar tests: what it says while loading, about the
/// selection, and in the window's title.
final class StatusBarTests: XCTestCase {
    @MainActor
    func testItSaysLoadingUntilTheFolderIsIn() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.selectedSourceID = library.folderID("d")
        XCTAssertEqual(model.statusText, "Loading…")
        try await waitUntil("the folder to load") { !model.isLoading }
        XCTAssertEqual(model.statusText, "1 item")
    }

    @MainActor
    func testRefreshSaysThePlaceAndHowManyItems() async throws {
        let library = try await makeLibrary()
        library.model.refresh()
        try await waitUntil("the refresh to finish") { library.said.contains { $0.text == "Photos, 4 items" } }
    }

    @MainActor
    func testTheSelectionIsCountedAgainstWhatSearchShows() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.searchText = "jpg"
        model.selection = [library.id("a.jpg")]
        XCTAssertEqual(model.statusText, "1 of 2 items selected")
    }

    @MainActor
    func testASelectionHiddenBySearchIsntCounted() async throws {
        // Found by #21: it read "2 of 1 item selected", while Favorite and the rest acted on both. Rows a search hides
        // now leave the selection, as on Windows, and don't come back when the search is cleared.
        let library = try await makeLibrary()
        let model = library.model
        model.selection = [library.id("a.jpg"), library.id("c.jpg")]
        model.searchText = "mov"
        XCTAssertEqual(model.statusText, "1 item")
        XCTAssertTrue(model.selection.isEmpty)
        model.searchText = ""
        XCTAssertTrue(model.selection.isEmpty)
        model.selection = [library.id("a.jpg"), library.id("c.jpg")]
        model.searchText = "a.jpg"
        XCTAssertEqual(model.selection, [library.id("a.jpg")])
        XCTAssertEqual(model.statusText, "1 of 1 item selected")
    }

    @MainActor
    func testTheWindowTitleNamesThePlaceTheAppAndTheMode() async throws {
        let library = try await makeLibrary()
        XCTAssertEqual(library.model.windowTitle, "Photos - \(AppInfo.name) - Read-Only Mode")
        library.model.selectedSourceID = nil
        XCTAssertEqual(library.model.windowTitle, "\(AppInfo.name) - \(AppInfo.name) - Read-Only Mode",
                       "nothing showing: the app's name stands in for the place")
    }

    @MainActor
    func testTheBackgroundSectionShowsWhatItsDoingBeforeWhyItStopped() async throws {
        let library = try await makeLibrary()
        let model = library.model
        XCTAssertNil(model.backgroundStatus)
        model.backgroundProblem = "Background describing stopped"
        XCTAssertEqual(model.backgroundStatus, "Background describing stopped")
        model.backgroundProgress = "Describing photos in the background, 1 of 3"
        XCTAssertEqual(model.backgroundStatus, "Describing photos in the background, 1 of 3")
        model.backgroundProgress = nil
        XCTAssertEqual(model.backgroundStatus, "Background describing stopped")
    }

    @MainActor
    func testWithNothingShowingTheStatusBarSaysNoItems() async throws {
        let library = try await makeLibrary()
        let model = library.model
        model.selectedSourceID = nil
        XCTAssertEqual(model.statusText, "0 items")
        model.readStatusBar()
        XCTAssertEqual(library.said.last?.text, "0 items")
    }
}
