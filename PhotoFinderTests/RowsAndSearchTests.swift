import XCTest
@testable import Photo_Finder

/// The list's rows (#21), as Windows' ModelTests and RowQueryTests cover them: what each column says, how the
/// columns sort, and what a search finds. Rows are made from files and sidebar items, so no Photos library is needed.
final class RowsAndSearchTests: XCTestCase {
    private static let march2024 = DateComponents(calendar: .current, year: 2024, month: 3, day: 2, hour: 10).date!

    private func photo(_ name: String, taken: Date? = march2024, modified: Date = march2024, size: Int64 = 1,
                       metadata: FileMetadata? = nil) -> FileRow {
        var details = metadata ?? FileMetadata()
        if metadata == nil || details.dateTaken == nil { details.dateTaken = taken }
        return FileRow(file: MediaFile(path: "/Pictures/Trips/\(name)", size: size, created: nil, modified: modified),
                       metadata: details)
    }

    private func folder(_ name: String, count: Int = 1) -> FileRow {
        FileRow(container: SidebarItem(id: FolderID.folder("/Pictures/\(name)"), title: name, kind: .directory("/Pictures/\(name)"),
                                       systemImage: "folder", count: count, children: nil, parentID: nil,
                                       containerKind: "Folder"))
    }

    private func sorted(_ rows: [FileRow], by id: String, descending: Bool = false) -> [String] {
        var comparator = ColumnOrder.comparator(id)
        comparator.order = descending ? .reverse : .forward
        return rows.sorted(using: [comparator]).map(\.name)
    }

    // MARK: - What a row says

    func testAPhotoRowFormatsItsColumns() {
        var metadata = FileMetadata()
        metadata.width = 4032
        metadata.height = 3024
        metadata.caption = "Birthday cake"
        metadata.keywords = ["family", "party"]
        metadata.rating = 4
        metadata.latitude = 47.60621
        metadata.longitude = -122.33207
        let row = photo("IMG_9.jpg", metadata: metadata)
        XCTAssertEqual(row.id, "file:/Pictures/Trips/IMG_9.jpg")
        XCTAssertEqual(row.kind, "Photo")
        XCTAssertEqual(row.dimensions, "4032 × 3024")
        XCTAssertEqual(row.orientation, "Landscape")
        XCTAssertEqual(row.megapixelsText, "\(12.2.formatted(.number.precision(.fractionLength(1)))) MP")
        XCTAssertEqual(row.caption, "Birthday cake")
        XCTAssertEqual(row.keywords, "family, party")
        XCTAssertEqual(row.ratingText, "4 stars")
        XCTAssertEqual(row.locationText, "47.6062, -122.3321")
        XCTAssertEqual(row.source, "Trips")
        XCTAssertEqual(row.durationText, "")
        XCTAssertFalse(row.isContainer)
    }

    func testOrientationAndOneStar() {
        var tall = FileMetadata()
        tall.width = 1000
        tall.height = 2000
        tall.rating = 1
        XCTAssertEqual(photo("a.jpg", metadata: tall).orientation, "Portrait")
        XCTAssertEqual(photo("a.jpg", metadata: tall).ratingText, "1 star")
        var square = FileMetadata()
        square.width = 500
        square.height = 500
        XCTAssertEqual(photo("a.jpg", metadata: square).orientation, "Square")
        XCTAssertEqual(photo("a.jpg", metadata: square).ratingText, "")
    }

    func testAVideoRowShowsItsLengthButNotMegapixels() {
        var metadata = FileMetadata()
        metadata.width = 1920
        metadata.height = 1080
        metadata.duration = 75.4
        let row = photo("clip.mov", metadata: metadata)
        XCTAssertEqual(row.kind, "Video")
        XCTAssertEqual(row.durationText, FileRow.durationText(75))
        XCTAssertEqual(row.megapixelsText, "", "a video's picture size isn't in megapixels")
        XCTAssertEqual(row.dimensions, "1920 × 1080")
    }

    func testLengthsUseHoursOnlyWhenNeeded() {
        XCTAssertEqual(FileRow.durationText(5), "0:05")
        XCTAssertEqual(FileRow.durationText(75), "1:15")
        XCTAssertEqual(FileRow.durationText(75.6), "1:16", "rounded to the second")
        XCTAssertEqual(FileRow.durationText(3_725), "1:02:05")
    }

    func testDateTakenFallsBackToWhenTheFileWasMadeThenModified() {
        let made = Date(timeIntervalSince1970: 1_000_000), modified = Date(timeIntervalSince1970: 2_000_000)
        let taken = Date(timeIntervalSince1970: 500_000)
        var metadata = FileMetadata()
        metadata.dateTaken = taken
        let file = MediaFile(path: "/p/a.jpg", size: 1, created: made, modified: modified)
        XCTAssertEqual(FileRow(file: file, metadata: metadata).date, taken)
        XCTAssertEqual(FileRow(file: file, metadata: FileMetadata()).date, made)
        let noCreated = MediaFile(path: "/p/a.jpg", size: 1, created: nil, modified: modified)
        XCTAssertEqual(FileRow(file: noCreated, metadata: nil).date, modified)
    }

    func testAFolderRowSaysWhatsInsideIt() {
        XCTAssertEqual(folder("Trips", count: 1).kind, "Folder, 1 item")
        XCTAssertEqual(folder("Trips", count: 0).kind, "Folder, 0 items")
        XCTAssertEqual(folder("Trips", count: 1_234).kind, "Folder, \(1_234.formatted()) items")
        let row = folder("Trips")
        XCTAssertTrue(row.isContainer)
        XCTAssertEqual(row.name, "Trips")
        XCTAssertEqual(row.fileType, "Folder")
        XCTAssertNil(row.date)
        XCTAssertEqual(row.dateText, "")
    }

    func testYearAndMonthRowsAreDatedForSorting() {
        let year = FileRow(container: SidebarItem(id: "year:2023", title: "2023", kind: .year(2023), systemImage: "folder",
                                                  count: 3, children: [], parentID: nil, containerKind: "Year"))
        let month = FileRow(container: SidebarItem(id: "month:2024-7", title: "July 2024", kind: .month(year: 2024, month: 7),
                                                   systemImage: "calendar", count: 1, children: nil, parentID: "year:2024",
                                                   containerKind: "Month"))
        XCTAssertEqual(year.date, DateComponents(calendar: .current, year: 2023, month: 1, day: 1).date)
        XCTAssertEqual(month.date, DateComponents(calendar: .current, year: 2024, month: 7, day: 1).date)
        XCTAssertEqual(month.kind, "Month, 1 item")
        XCTAssertEqual(sorted([year, month], by: "date", descending: true), ["July 2024", "2023"])
    }

    func testWhatsSpokenIsNameKindDateAndCaption() {
        var metadata = FileMetadata()
        metadata.caption = "A cake"
        let row = photo("a.jpg", metadata: metadata)
        XCTAssertEqual(row.spokenDescription, "a.jpg, Photo, \(row.dateText), Caption: A cake")
        XCTAssertEqual(folder("Trips", count: 2).spokenDescription, "Trips, Folder, 2 items", "no empty date or caption")
    }

    func testTheDescriptionColumnIsFilledOnlyWhenAsked() {
        let row = photo("a.jpg")
        XCTAssertEqual(row.descriptionText, "")
        let filled = row.withDescription("A cat on a sofa.")
        XCTAssertEqual(filled.descriptionText, "A cat on a sofa.")
        XCTAssertEqual(filled.name, row.name, "nothing else changes")
        XCTAssertEqual(filled.id, row.id)
    }

    // MARK: - Sorting

    func testNamesSortNaturallyAsInFinder() {
        let rows = [photo("IMG_10.jpg"), photo("IMG_2.jpg"), photo("img_1.jpg")]
        XCTAssertEqual(sorted(rows, by: "name"), ["img_1.jpg", "IMG_2.jpg", "IMG_10.jpg"])
        XCTAssertEqual(sorted(rows, by: "name", descending: true), ["IMG_10.jpg", "IMG_2.jpg", "img_1.jpg"])
    }

    func testDatesSortNewestFirstWithUndatedRowsLast() {
        let rows = [photo("old.jpg", taken: Date(timeIntervalSince1970: 1_000_000_000)),
                    folder("Undated"),
                    photo("new.jpg", taken: Date(timeIntervalSince1970: 1_700_000_000))]
        XCTAssertEqual(sorted(rows, by: "date", descending: true), ["new.jpg", "old.jpg", "Undated"])
        XCTAssertEqual(sorted(rows, by: "date"), ["Undated", "old.jpg", "new.jpg"])
    }

    func testDimensionsSortByPixelsNotByTheirText() {
        var small = FileMetadata(), large = FileMetadata()
        small.width = 900
        small.height = 900
        large.width = 1000
        large.height = 1000
        let rows = [photo("small.jpg", metadata: small), photo("large.jpg", metadata: large)]
        XCTAssertEqual(sorted(rows, by: "dimensions", descending: true), ["large.jpg", "small.jpg"],
                       "\"900 × 900\" would come after \"1000 × 1000\" as text")
        XCTAssertEqual(sorted(rows, by: "megapixels", descending: true), ["large.jpg", "small.jpg"])
    }

    func testRatingsAndLengthsSortByNumber() {
        var two = FileMetadata(), ten = FileMetadata()
        two.rating = 2
        ten.rating = 5
        XCTAssertEqual(sorted([photo("two.jpg", metadata: two), photo("five.jpg", metadata: ten)], by: "rating", descending: true),
                       ["five.jpg", "two.jpg"])
        var short = FileMetadata(), long = FileMetadata()
        short.duration = 9
        long.duration = 61
        XCTAssertEqual(sorted([photo("short.mov", metadata: short), photo("long.mov", metadata: long)], by: "duration", descending: true),
                       ["long.mov", "short.mov"])
    }

    func testDateModifiedSortsByTheFilesDate() {
        let rows = [photo("a.jpg", modified: Date(timeIntervalSince1970: 1_000)),
                    photo("b.jpg", modified: Date(timeIntervalSince1970: 3_000)),
                    photo("c.jpg", modified: Date(timeIntervalSince1970: 2_000))]
        XCTAssertEqual(sorted(rows, by: "modified", descending: true), ["b.jpg", "c.jpg", "a.jpg"])
    }

    // MARK: - Searching

    func testSearchMatchesNameKindCaptionKeywordsAndDateIgnoringCase() {
        var metadata = FileMetadata()
        metadata.caption = "Birthday cake"
        metadata.keywords = ["family"]
        let row = photo("IMG_9.jpg", metadata: metadata)
        XCTAssertTrue(LibraryModel.matchesSearch(row, "img_9"))
        XCTAssertTrue(LibraryModel.matchesSearch(row, "photo"), "the kind")
        XCTAssertTrue(LibraryModel.matchesSearch(row, "CAKE"), "the caption")
        XCTAssertTrue(LibraryModel.matchesSearch(row, "Family"), "the keywords")
        XCTAssertTrue(LibraryModel.matchesSearch(row, "2024"), "the date")
        XCTAssertFalse(LibraryModel.matchesSearch(row, "zebra"))
    }

    func testSearchIgnoresTheCaseOfAccentedLetters() {
        var metadata = FileMetadata()
        metadata.caption = "Café in Zürich"
        let row = photo("a.jpg", metadata: metadata)
        XCTAssertTrue(LibraryModel.matchesSearch(row, "CAFÉ"))
        XCTAssertTrue(LibraryModel.matchesSearch(row, "zürich"))
    }

    func testSearchFindsAFolderByItsKindAndCount() {
        let row = folder("Trips", count: 3)
        XCTAssertTrue(LibraryModel.matchesSearch(row, "folder"))
        XCTAssertTrue(LibraryModel.matchesSearch(row, "3 items"))
        XCTAssertFalse(LibraryModel.matchesSearch(row, "photo"))
    }

    func testSearchDoesntLookInDescriptionsOrAltText() {
        // As on Windows, a search is of what the list shows by default; descriptions aren't searched.
        var metadata = FileMetadata()
        metadata.altText = "A zebra"
        let row = photo("a.jpg", metadata: metadata).withDescription("A giraffe")
        XCTAssertFalse(LibraryModel.matchesSearch(row, "zebra"))
        XCTAssertFalse(LibraryModel.matchesSearch(row, "giraffe"))
    }
}
