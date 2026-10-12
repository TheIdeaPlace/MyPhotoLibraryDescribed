import AppKit
import os
import Photos
import SwiftUI

struct CaptionTarget: Identifiable {
    let id = UUID()
    let assetIDs: [String]
    let initialText: String
}

enum ViewMode: String {
    case list, gallery
}

struct InfoTarget: Identifiable {
    /// A photo in the Photos library, or a file in an added folder (#36).
    enum Source {
        case asset(PHAsset)
        case file(MediaFile, FileRow)
    }

    let id: String
    let source: Source
    let name: String

    /// The key its description is saved under.
    var descriptionKey: String {
        switch source {
        case .asset(let asset): asset.localIdentifier
        case .file(let file, _): file.descriptionKey
        }
    }

    var isImage: Bool {
        switch source {
        case .asset(let asset): asset.mediaType == .image
        case .file(let file, _): !file.isVideo
        }
    }
}

@MainActor
@Observable
final class LibraryModel {
    enum AuthState { case unknown, authorized, denied }

    var authState: AuthState = .unknown
    private(set) var sections: [SidebarSection] = []
    private(set) var itemsByID: [String: SidebarItem] = [:]
    var expandedIDs: Set<String> = []

    var selectedSourceID: String? {
        didSet {
            if oldValue != selectedSourceID { loadSelectedSource(keepSelection: false) }
        }
    }

    private(set) var rows: [FileRow] = [] {
        didSet { if !fillingDescriptions, showsDescriptionColumn { fillDescriptions() } }
    }
    @ObservationIgnored private var fillingDescriptions = false
    /// View > Show Description: the Description panel beside the list or gallery (#50). On unless turned off.
    var showDescriptionPanel: Bool = UserDefaults.standard.object(forKey: "showDescriptionPanel") as? Bool ?? true {
        didSet { UserDefaults.standard.set(showDescriptionPanel, forKey: "showDescriptionPanel") }
    }
    /// Photos being described right now, by row id and how many describes are running on each, so the Description
    /// panel can say "Describing…" until the last one finishes.
    private(set) var describingIDs: [String: Int] = [:]
    var sortOrder: [KeyPathComparator<FileRow>] = [KeyPathComparator(\FileRow.sortDate, order: .reverse)] {
        didSet { rows.sort(using: sortOrder) }
    }
    var selection: Set<String> = [] {
        didSet { if selection != oldValue { autoDescriber.moved(to: autoDescribeCandidate) } }
    }
    var searchText = "" {
        didSet { if searchText != oldValue { announceResultsAfterPause() } }
    }
    @ObservationIgnored private var resultsAnnouncement: Task<Void, Never>?
    var searchFocusRequest = 0
    /// Go > Next Pane and Previous Pane; the browser moves focus when this changes.
    private(set) var paneRequest = PaneRequest()
    private(set) var isLoading = false
    private(set) var activity: String?
    private(set) var nameProgress: String?
    var previewURL: URL?
    private(set) var previewURLs: [URL] = []
    var infoTarget: InfoTarget? {
        didSet { if infoTarget == nil, oldValue != nil { autoDescriber.refocused(currentID: autoDescribeCandidate) } }
    }
    var captionTarget: CaptionTarget? {
        didSet { if captionTarget == nil, oldValue != nil { autoDescriber.refocused(currentID: autoDescribeCandidate) } }
    }
    var viewMode: ViewMode = .list {
        didSet {
            UserDefaults.standard.set(viewMode.rawValue, forKey: "viewMode")
            if viewMode == .gallery { ensureGallerySelection() }
        }
    }
    /// The columns shown, in the order they're shown and read. Always includes Name.
    var columnOrder: [String] = ColumnOrder.defaultOrder {
        didSet {
            UserDefaults.standard.set(columnOrder, forKey: "columnOrder")
            if showsDescriptionColumn, !oldValue.contains("description") { fillDescriptions() }
        }
    }
    var columnsSheetShown = false {
        didSet { if !columnsSheetShown, oldValue { autoDescriber.refocused(currentID: autoDescribeCandidate) } }
    }
    /// What describing new photos in the background is doing, shown in the status bar.
    var backgroundProgress: String?
    var errorMessage: String?

    @ObservationIgnored private var assetsByID: [String: PHAsset] = [:]
    /// The files listed when an added folder is showing, by row id (#36). Empty for Photos' albums.
    @ObservationIgnored private var filesByID: [String: MediaFile] = [:]
    /// Folders added to the library (File > Add Folder to Library…), in the order they were added.
    private(set) var folderRoots: [String] = FolderRoots.load()
    /// The Folders section's tree, from the last scan of the added folders.
    @ObservationIgnored private var folderNodes: [FolderNode] = []
    /// The scan of each added folder in progress, by lowercased path; a newer scan of the same folder cancels it.
    @ObservationIgnored private var folderScans: [String: Task<FolderNode, Never>] = [:]
    /// Metadata already read from files, so going back to a folder doesn't read it all again. Keyed like
    /// descriptions, so an edited file is read afresh.
    @ObservationIgnored private var fileMetadata: [String: FileMetadata] = [:]
    /// Watches the folder showing for files added, removed or renamed.
    @ObservationIgnored private var folderWatcher: DispatchSourceFileSystemObject?
    @ObservationIgnored private var folderChangeTask: Task<Void, Never>?
    /// Said before "All Photos, 12 items" once a removed folder's place has loaded.
    @ObservationIgnored private var loadAnnouncementPrefix: String?
    @ObservationIgnored private var currentFetch: PHFetchResult<PHAsset>?
    @ObservationIgnored private var loadGeneration = 0
    @ObservationIgnored private var pendingSelection: String?
    @ObservationIgnored private var announceNextLoad = false
    @ObservationIgnored private var observer: LibraryObserver?
    @ObservationIgnored private var dateBuckets: [Int: [Int: Int]] = [:]
    @ObservationIgnored private var allPhotosCount = 0
    @ObservationIgnored private var refreshTask: Task<Void, Never>?
    @ObservationIgnored private var nameFillTask: Task<Void, Never>?
    private let names = NameCache()
    /// Apple Intelligence descriptions already made, shared by Get Info and background describing.
    let descriptions = DescriptionCache()
    /// Every photo, watched for new ones to describe in the background.
    @ObservationIgnored private var allPhotosFetch: PHFetchResult<PHAsset>?
    /// The background describer (macOS 27), kept untyped so this class builds with older SDKs too.
    @ObservationIgnored private var backgroundDescriber: AnyObject?
    /// Describe Photo in progress; a newer one replaces it.
    @ObservationIgnored private var describeTask: Task<Void, Never>?
    /// Photos Describe Again is describing: an automatic description of one finishing meanwhile is older, so it's
    /// neither saved nor spoken.
    @ObservationIgnored private var describingAgain: [String: Int] = [:]
    @ObservationIgnored private var terminationObserver: NSObjectProtocol?
    @ObservationIgnored private var activationObserver: NSObjectProtocol?
    @ObservationIgnored private var descriptionObserver: NSObjectProtocol?
    /// Changes whenever a description is saved, so views showing one (the gallery) update.
    private(set) var descriptionRevision = 0
    /// Photo > Describe Photos Automatically; on unless turned off.
    var describeAutomatically: Bool = UserDefaults.standard.object(forKey: "describeAutomatically") as? Bool ?? true {
        didSet { UserDefaults.standard.set(describeAutomatically, forKey: "describeAutomatically") }
    }
    @ObservationIgnored private lazy var autoDescriber = makeAutoDescriber()

    /// Describe photos added to the Photos library behind the scenes, so their descriptions are ready in Get Info.
    var describeInBackground: Bool = UserDefaults.standard.bool(forKey: "describeInBackground") {
        didSet { UserDefaults.standard.set(describeInBackground, forKey: "describeInBackground") }
    }
    nonisolated private static let log = Logger(subsystem: "net.theideaplace.PhotoFinder", category: "library")
    private let imageManager = PHCachingImageManager()
    private let galleryImageSize = CGSize(width: 2400, height: 2400)
    private let galleryImageOptions: PHImageRequestOptions = {
        let options = PHImageRequestOptions()
        options.deliveryMode = .highQualityFormat
        options.isNetworkAccessAllowed = true
        return options
    }()

    init() {
        if let raw = UserDefaults.standard.string(forKey: "viewMode"), let mode = ViewMode(rawValue: raw) {
            viewMode = mode
        }
        if let saved = UserDefaults.standard.stringArray(forKey: "columnOrder") {
            columnOrder = ColumnOrder.clean(saved)
        } else if let data = UserDefaults.standard.data(forKey: "columns"),
                  let migrated = ColumnOrder.migrate(customization: data) {
            // Columns from before they could be reordered: keep which were on, in the default order.
            columnOrder = migrated
        }
        // Descriptions are saved a couple of seconds after each change; don't lose one made just before quitting.
        let cache = descriptions
        terminationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification, object: nil, queue: .main) { _ in cache.save() }
        // Back from another app: the photo you're on is described if you left during the pause.
        activationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.autoDescriber.refocused(currentID: self.autoDescribeCandidate)
            }
        }
        descriptionObserver = NotificationCenter.default.addObserver(
            forName: DescriptionCache.didChange, object: cache, queue: .main) { [weak self] note in
            let key = note.userInfo?[DescriptionCache.keyInfo] as? String
            let text = note.userInfo?[DescriptionCache.textInfo] as? String
            MainActor.assumeIsolated {
                self?.descriptionRevision += 1
                if self?.showsDescriptionColumn == true { self?.fillDescriptions() }
                if let key, let text { self?.captionFromNewDescription(text, key: key) }
            }
        }
    }

    /// The saved description of a photo, for showing it. Updates when one is saved.
    func savedDescription(for id: String) -> String? {
        _ = descriptionRevision
        guard let key = descriptionKey(for: id) else { return nil }
        return descriptions.description(forKey: key)
    }

    /// The key a row's description is saved under: its Photos identifier, or for a file in an added folder its
    /// path, size and modified time (#36). Nil for folders and rows no longer listed.
    private func descriptionKey(for id: String) -> String? {
        if let asset = assetsByID[id] { return asset.localIdentifier }
        return filesByID[id]?.descriptionKey
    }

    /// Whether a row is a photo (not a video), from Photos or a folder.
    private func isPhoto(_ id: String) -> Bool {
        if let asset = assetsByID[id] { return asset.mediaType == .image }
        if let file = filesByID[id] { return !file.isVideo }
        return false
    }

    // MARK: - Describe Photos Automatically

    /// Photo > Describe Photos Automatically.
    func setDescribeAutomatically(_ on: Bool) {
        describeAutomatically = on
        if on {
            autoDescriber.turnedOn(currentID: autoDescribeCandidate)
        } else {
            autoDescriber.isEnabled = false
            announce("Photos won't be described automatically")
        }
    }

    /// The photo automatic describing is about: the one photo selected and shown. Nothing for folders, videos,
    /// several items, or a photo hidden by Search.
    private var autoDescribeCandidate: String? {
        guard selection.count == 1, let id = selection.first, isPhoto(id),
              let row = (searchText.isEmpty ? rows : visibleRows).first(where: { $0.id == id }), !row.isContainer
        else { return nil }
        return id
    }

    private func makeAutoDescriber() -> AutoDescriber {
        let describer = AutoDescriber(isEnabled: describeAutomatically)
        describer.saved = { [weak self] id in
            guard let self, let key = self.descriptionKey(for: id) else { return nil }
            return self.descriptions.description(forKey: key)
        }
        describer.unavailableReason = { [weak self] in self?.describeUnavailableReason }
        describer.describe = { [weak self] id in
            guard let self else { return nil }
            // Not downloaded from iCloud just because you arrowed past it, as on Windows; Describe Photo does.
            return await self.describeAndRemember(id, allowNetwork: false) { [weak self] in
                (self?.describingAgain[id] ?? 0) == 0
            }
        }
        describer.isStillOn = { [weak self] id in
            // Not while another app, Get Info or a sheet is in front: you've moved on from the photo for now.
            guard let self, NSApplication.shared.isActive, NSApplication.shared.keyWindow?.isSheet == false,
                  self.infoTarget == nil, self.captionTarget == nil, !self.columnsSheetShown else { return false }
            // Describe Again is working on it: leave it alone, even if you arrow away and back, so the old
            // description isn't spoken first (as on Windows). Describe Again speaks the new one.
            guard (self.describingAgain[id] ?? 0) == 0 else { return false }
            return self.autoDescribeCandidate == id
        }
        describer.speak = { [weak self] text in self?.announce(text, queued: true) }
        return describer
    }

    // MARK: - Search and sort

    /// How many results a search found, once typing pauses, as on Windows: "No results", "1 result", "12 results".
    private func announceResultsAfterPause() {
        resultsAnnouncement?.cancel()
        guard !searchText.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        resultsAnnouncement = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(600))
            // Still loading the list (a big album or folder): count once it's in, not "No results" now.
            for _ in 0..<150 where self?.isLoading == true && !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(200))
            }
            guard let self, !Task.isCancelled else { return }
            self.announce(Self.resultCount(self.visibleRows.count), queued: true)
        }
    }

    nonisolated static func resultCount(_ count: Int) -> String {
        count == 0 ? "No results" : count == 1 ? "1 result" : "\(count.formatted()) results"
    }

    /// The column the list is sorted by, for View > Sort By's checkmark.
    var sortColumnID: String? { ColumnOrder.sortColumn(of: sortOrder) }

    /// View > Sort By: sorts by a column without its header. Choosing the current column again reverses it.
    func sortBy(_ id: String) {
        let descending = sortColumnID == id ? sortOrder.first?.order != .reverse : ColumnOrder.sortsDescendingFirst(id)
        var comparator = ColumnOrder.comparator(id)
        comparator.order = descending ? .reverse : .forward
        sortOrder = [comparator]
        if id == "description" { fillDescriptions() }
        let title = ColumnOrder.info(id)?.title ?? id
        announce("Sorted by \(title), \(descending ? "descending" : "ascending")")
    }

    // MARK: - Columns

    var shownColumns: [ColumnInfo] { columnOrder.compactMap(ColumnOrder.info) }

    /// Shows these columns, in this order: in the list, and in the order VoiceOver reads each row's cells.
    func setColumnOrder(_ ids: [String]) {
        columnOrder = ColumnOrder.clean(ids)
    }

    // MARK: - Derived state

    var visibleRows: [FileRow] {
        let query = searchText.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else { return rows }
        return rows.filter {
            $0.name.localizedCaseInsensitiveContains(query)
                || $0.kind.localizedCaseInsensitiveContains(query)
                || $0.caption.localizedCaseInsensitiveContains(query)
                || $0.keywords.localizedCaseInsensitiveContains(query)
                || $0.dateText.localizedCaseInsensitiveContains(query)
        }
    }

    var currentItem: SidebarItem? { selectedSourceID.flatMap { itemsByID[$0] } }
    var currentTitle: String { currentItem?.title ?? AppInfo.name }

    var statusText: String {
        if isLoading { return "Loading…" }
        let count = visibleRows.count
        let items = count == 1 ? "1 item" : "\(count.formatted()) items"
        return selection.isEmpty ? items : "\(selection.count.formatted()) of \(items) selected"
    }

    // MARK: - Startup

    func start() async {
        guard authState == .unknown else { return }
        let status = await PHPhotoLibrary.requestAuthorization(for: .readWrite)
        guard status == .authorized || status == .limited else {
            authState = .denied
            return
        }
        authState = .authorized
        try? FileManager.default.removeItem(at: PhotoExporter.temporaryRoot)

        let observer = LibraryObserver { [weak self] change in
            Task { @MainActor in self?.libraryDidChange(change.value) }
        }
        self.observer = observer
        PHPhotoLibrary.shared().register(observer)

        rebuildSidebar()
        selectedSourceID = SidebarID.allPhotos
        refreshFolders()
        // Watched for new photos only while describing them in the background is on: it's a whole-library fetch.
        if describeInBackground { allPhotosFetch = PHAsset.fetchAssets(with: .image, options: nil) }
        await refreshDateBuckets()
        rebuildSidebar()
        warmNameCache()
    }

    // MARK: - Sidebar

    private static let hiddenSmartAlbums: Set<Int> = [
        PHAssetCollectionSubtype.smartAlbumUserLibrary.rawValue,
        PHAssetCollectionSubtype.smartAlbumAllHidden.rawValue,
        1_000_000_201, // Recently Deleted
    ]

    private static let smartAlbumOrder: [PHAssetCollectionSubtype] = [
        .smartAlbumFavorites, .smartAlbumRecentlyAdded, .smartAlbumVideos, .smartAlbumLivePhotos,
        .smartAlbumScreenshots, .smartAlbumSelfPortraits, .smartAlbumDepthEffect, .smartAlbumPanoramas,
        .smartAlbumBursts, .smartAlbumSlomoVideos, .smartAlbumTimelapses, .smartAlbumCinematic,
        .smartAlbumLongExposures, .smartAlbumAnimated, .smartAlbumRAW,
    ]

    private static func symbol(for subtype: PHAssetCollectionSubtype) -> String {
        switch subtype {
        case .smartAlbumFavorites: "heart"
        case .smartAlbumRecentlyAdded: "clock"
        case .smartAlbumVideos, .smartAlbumCinematic: "video"
        case .smartAlbumLivePhotos: "livephoto"
        case .smartAlbumScreenshots: "camera.viewfinder"
        case .smartAlbumSelfPortraits: "person.crop.square"
        case .smartAlbumDepthEffect: "cube"
        case .smartAlbumPanoramas: "pano"
        case .smartAlbumBursts: "square.stack.3d.down.right"
        case .smartAlbumSlomoVideos: "slowmo"
        case .smartAlbumTimelapses: "timelapse"
        default: "rectangle.stack"
        }
    }

    /// Rebuilds the sidebar from Photos and the last scan of the added folders. Returns whether the place showing
    /// was reloaded because it changed.
    @discardableResult
    private func rebuildSidebar() -> Bool {
        let previousItem = currentItem

        allPhotosCount = PHAsset.fetchAssets(with: nil).count
        var library = [SidebarItem(id: SidebarID.allPhotos, title: "All Photos", kind: .allPhotos,
                                   systemImage: "photo.on.rectangle", count: allPhotosCount,
                                   children: nil, parentID: nil, containerKind: "Library")]

        let smart = PHAssetCollection.fetchAssetCollections(with: .smartAlbum, subtype: .any, options: nil)
        var ranked: [(rank: Int, item: SidebarItem)] = []
        for i in 0..<smart.count {
            let collection = smart.object(at: i)
            let subtype = collection.assetCollectionSubtype
            guard !Self.hiddenSmartAlbums.contains(subtype.rawValue) else { continue }
            let count = PHAsset.fetchAssets(in: collection, options: nil).count
            guard count > 0 else { continue }
            let rank = Self.smartAlbumOrder.firstIndex(of: subtype) ?? Int.max
            ranked.append((rank, albumItem(collection, count: count, parentID: nil, symbol: Self.symbol(for: subtype))))
        }
        library += ranked.sorted { ($0.rank, $0.item.title) < ($1.rank, $1.item.title) }.map(\.item)

        var dates: [SidebarItem] = []
        for year in dateBuckets.keys.sorted(by: >) {
            let months = dateBuckets[year] ?? [:]
            let yearID = "year:\(year)"
            let children = months.keys.sorted(by: >).map { month in
                SidebarItem(id: "month:\(year)-\(month)", title: Self.monthTitle(year: year, month: month),
                            kind: .month(year: year, month: month), systemImage: "calendar",
                            count: months[month] ?? 0, children: nil, parentID: yearID, containerKind: "Month")
            }
            dates.append(SidebarItem(id: yearID, title: String(year), kind: .year(year), systemImage: "folder",
                                     count: months.values.reduce(0, +), children: children, parentID: nil,
                                     containerKind: "Year"))
        }

        let albums = collectionItems(PHCollectionList.fetchTopLevelUserCollections(with: nil), parentID: nil)

        let sharedResult = PHAssetCollection.fetchAssetCollections(with: .album, subtype: .albumCloudShared, options: nil)
        var shared: [SidebarItem] = []
        for i in 0..<sharedResult.count {
            let collection = sharedResult.object(at: i)
            let count = PHAsset.fetchAssets(in: collection, options: nil).count
            shared.append(albumItem(collection, count: count, parentID: nil, symbol: "person.2"))
        }

        var newSections = [SidebarSection(id: "library", title: "Library", items: library)]
        if !dates.isEmpty { newSections.append(SidebarSection(id: "dates", title: "Dates", items: dates)) }
        if !albums.isEmpty { newSections.append(SidebarSection(id: "albums", title: "My Albums", items: albums)) }
        if !shared.isEmpty { newSections.append(SidebarSection(id: "shared", title: "Shared Albums", items: shared)) }
        // Folders added from outside Photos, below Photos' own sections (#36).
        let folders = FolderScanner.sidebarItems(folderNodes)
        if !folders.isEmpty { newSections.append(SidebarSection(id: "folders", title: "Folders", items: folders)) }

        guard newSections != sections else { return false }
        sections = newSections

        var index: [String: SidebarItem] = [:]
        func add(_ items: [SidebarItem]) {
            for item in items {
                index[item.id] = item
                if let children = item.children { add(children) }
            }
        }
        newSections.forEach { add($0.items) }
        itemsByID = index

        if let id = selectedSourceID, index[id] == nil {
            selectedSourceID = SidebarID.allPhotos
        } else if let item = currentItem, item.children != nil || item.isDirectory, item != previousItem {
            // A folder's contents changed while it was showing.
            loadSelectedSource(keepSelection: true)
            return true
        }
        return false
    }

    private func albumItem(_ collection: PHAssetCollection, count: Int, parentID: String?, symbol: String) -> SidebarItem {
        SidebarItem(id: "album:" + collection.localIdentifier, title: collection.localizedTitle ?? "Untitled Album",
                    kind: .assetCollection(collection.localIdentifier), systemImage: symbol, count: count,
                    children: nil, parentID: parentID, containerKind: "Album")
    }

    private func collectionItems(_ result: PHFetchResult<PHCollection>, parentID: String?) -> [SidebarItem] {
        var items: [SidebarItem] = []
        for i in 0..<result.count {
            let collection = result.object(at: i)
            if let album = collection as? PHAssetCollection {
                let count = PHAsset.fetchAssets(in: album, options: nil).count
                items.append(albumItem(album, count: count, parentID: parentID, symbol: "rectangle.stack"))
            } else if let list = collection as? PHCollectionList {
                let id = "folder:" + list.localIdentifier
                let children = collectionItems(PHCollection.fetchCollections(in: list, options: nil), parentID: id)
                items.append(SidebarItem(id: id, title: list.localizedTitle ?? "Untitled Folder",
                                         kind: .collectionList(list.localIdentifier), systemImage: "folder",
                                         count: children.count, children: children, parentID: parentID,
                                         containerKind: "Folder"))
            }
        }
        return items
    }

    private static func monthTitle(year: Int, month: Int) -> String {
        guard let date = Calendar.current.date(from: DateComponents(year: year, month: month, day: 1)) else {
            return "\(month)/\(year)"
        }
        return date.formatted(.dateTime.month(.wide).year())
    }

    private func refreshDateBuckets() async {
        let buckets = await Task.detached(priority: .utility) { () -> [Int: [Int: Int]] in
            let result = PHAsset.fetchAssets(with: nil)
            let assets = result.objects(at: IndexSet(integersIn: 0..<result.count))
            let calendar = Calendar.current
            var buckets: [Int: [Int: Int]] = [:]
            for asset in assets {
                guard let date = asset.creationDate else { continue }
                let parts = calendar.dateComponents([.year, .month], from: date)
                guard let year = parts.year, let month = parts.month else { continue }
                buckets[year, default: [:]][month, default: 0] += 1
            }
            return buckets
        }.value
        dateBuckets = buckets
    }

    /// Looks up every original file name in the background so large albums open quickly later.
    private func warmNameCache() {
        // macOS 27 hands out file names in bulk, so there's nothing to warm.
        if #available(macOS 27, *) { return }
        let names = self.names
        Task.detached(priority: .background) {
            let result = PHAsset.fetchAssets(with: nil)
            var looked = 0
            for i in 0..<result.count {
                let asset = result.object(at: i)
                guard names.cachedName(for: asset.localIdentifier) == nil else { continue }
                _ = names.name(for: asset)
                looked += 1
                if looked % 500 == 0 { names.save() }
            }
            names.save()
        }
    }

    // MARK: - Loading the file list

    private func fetchResult(for kind: SourceKind) -> PHFetchResult<PHAsset>? {
        let options = RowBuilder.fetchOptions()
        options.sortDescriptors = [NSSortDescriptor(key: "creationDate", ascending: true)]
        switch kind {
        case .allPhotos:
            return PHAsset.fetchAssets(with: options)
        case .assetCollection(let id):
            guard let collection = PHAssetCollection.fetchAssetCollections(withLocalIdentifiers: [id], options: nil).firstObject else {
                return nil
            }
            return PHAsset.fetchAssets(in: collection, options: options)
        case .month(let year, let month):
            let calendar = Calendar.current
            guard let start = calendar.date(from: DateComponents(year: year, month: month, day: 1)),
                  let end = calendar.date(byAdding: .month, value: 1, to: start) else { return nil }
            options.predicate = NSPredicate(format: "creationDate >= %@ AND creationDate < %@", start as NSDate, end as NSDate)
            return PHAsset.fetchAssets(with: options)
        case .collectionList, .year, .directory:
            return nil
        }
    }

    private func loadSelectedSource(keepSelection: Bool) {
        loadGeneration += 1
        let generation = loadGeneration
        nameFillTask?.cancel()
        nameProgress = nil
        let previousSelection = keepSelection ? selection : []
        if !keepSelection {
            selection = []
            searchText = ""
        }

        guard let item = currentItem else {
            rows = []
            currentFetch = nil
            assetsByID = [:]
            filesByID = [:]
            watchFolder(nil)
            isLoading = false
            return
        }

        if let path = item.directoryPath {
            loadFolder(item, path: path, previousSelection: previousSelection, keepSelection: keepSelection,
                       generation: generation)
            return
        }
        filesByID = [:]
        watchFolder(nil)

        if let children = item.children {
            currentFetch = nil
            assetsByID = [:]
            isLoading = false
            rows = children.map { FileRow(container: $0) }.sorted(using: sortOrder)
            finishLoad(previousSelection: previousSelection, item: item, slow: false)
            if viewMode == .gallery { ensureGallerySelection() }
            return
        }

        guard let fetch = fetchResult(for: item.kind) else {
            rows = []
            isLoading = false
            return
        }
        currentFetch = fetch
        isLoading = true
        if !keepSelection { rows = [] }

        let started = Date()
        let box = UncheckedBox(value: fetch)
        let names = self.names
        Task {
            let built = await Task.detached(priority: .userInitiated) {
                RowBuilder.build(box.value, names: names)
            }.value
            guard generation == self.loadGeneration else { return }
            Self.log.info("Loaded \(built.value.rows.count) items for \(item.title, privacy: .public) in \(Date().timeIntervalSince(started), format: .fixed(precision: 2))s; \(built.value.needingDetails.count) need details")
            self.assetsByID = built.value.assets
            self.rows = built.value.rows.sorted(using: self.sortOrder)
            self.isLoading = false
            self.finishLoad(previousSelection: previousSelection, item: item,
                            slow: Date().timeIntervalSince(started) > 1)
            if self.viewMode == .gallery { self.ensureGallerySelection() }
            // Fill details in the order they're listed, so the rows people reach first are done first.
            let pending = Set(built.value.needingDetails.map(\.localIdentifier))
            let ordered = self.rows.compactMap { pending.contains($0.id) ? self.assetsByID[$0.id] : nil }
            self.fillDetails(ordered, generation: generation)
        }
    }

    /// Reads names, captions and keywords a batch at a time, updating rows in place.
    private func fillDetails(_ assets: [PHAsset], generation: Int) {
        nameFillTask?.cancel()
        nameProgress = nil
        guard !assets.isEmpty else { return }
        let names = self.names
        nameFillTask = Task {
            let began = Date()
            var start = 0
            while start < assets.count {
                let end = min(start + 200, assets.count)
                let batch = UncheckedBox(value: Array(assets[start..<end]))
                let found = await Task.detached(priority: .userInitiated) {
                    batch.value.map { ($0.localIdentifier, AssetDetails.load(for: $0, names: names)) }
                }.value
                guard !Task.isCancelled, generation == self.loadGeneration else { return }
                let lookup = Dictionary(found, uniquingKeysWith: { first, _ in first })
                self.rows = self.rows.map { row in lookup[row.id].map { row.withDetails($0) } ?? row }
                self.nameProgress = "Reading names and captions, \(end.formatted()) of \(assets.count.formatted())…"
                start = end
            }
            self.nameProgress = nil
            Self.log.info("Read details for \(assets.count) items in \(Date().timeIntervalSince(began), format: .fixed(precision: 2))s")
            let detailColumns: [PartialKeyPath<FileRow>] = [\FileRow.name, \FileRow.caption, \FileRow.keywords, \FileRow.fileType]
            if let key = self.sortOrder.first?.keyPath, detailColumns.contains(key) {
                self.rows.sort(using: self.sortOrder)
            }
            names.save()
        }
    }

    private func finishLoad(previousSelection: Set<String>, item: SidebarItem, slow: Bool) {
        let ids = Set(rows.map(\.id))
        if let pending = pendingSelection {
            pendingSelection = nil
            selection = ids.contains(pending) ? [pending] : []
        } else {
            selection = previousSelection.intersection(ids)
        }
        if announceNextLoad || slow || loadAnnouncementPrefix != nil {
            announceNextLoad = false
            let prefix = loadAnnouncementPrefix ?? ""
            loadAnnouncementPrefix = nil
            announce("\(prefix)\(item.title), \(rows.count == 1 ? "1 item" : "\(rows.count.formatted()) items")")
        }
    }

    // MARK: - Folders (#36)

    /// Lists an added folder: its subfolders first as containers (from the sidebar, so they open the same place),
    /// then its photos and videos from the file system, with their metadata read afterwards a batch at a time.
    private func loadFolder(_ item: SidebarItem, path: String, previousSelection: Set<String>, keepSelection: Bool,
                            generation: Int) {
        currentFetch = nil
        assetsByID = [:]
        watchFolder(path)
        let containers = (item.children ?? []).map { FileRow(container: $0) }
        isLoading = true
        if !keepSelection {
            filesByID = [:]
            rows = containers.sorted(using: sortOrder)
        }
        let started = Date()
        Task {
            let files = await Task.detached(priority: .userInitiated) { FolderScanner.files(in: path) }.value
            guard generation == self.loadGeneration else { return }
            self.filesByID = Dictionary(files.map { (FolderID.file($0.path), $0) }, uniquingKeysWith: { first, _ in first })
            let fileRows = files.map { FileRow(file: $0, metadata: self.fileMetadata[$0.descriptionKey]) }
            self.rows = (containers + fileRows).sorted(using: self.sortOrder)
            self.isLoading = false
            self.finishLoad(previousSelection: previousSelection, item: item, slow: Date().timeIntervalSince(started) > 1)
            if self.viewMode == .gallery { self.ensureGallerySelection() }
            let pending = self.rows.compactMap { row in
                self.filesByID[row.id].flatMap { self.fileMetadata[$0.descriptionKey] == nil ? $0 : nil }
            }
            self.fillFileMetadata(pending, generation: generation)
        }
    }

    /// Reads each file's metadata off the main actor, a batch at a time in the order listed, updating rows in place.
    private func fillFileMetadata(_ files: [MediaFile], generation: Int) {
        nameFillTask?.cancel()
        nameProgress = nil
        guard !files.isEmpty else { return }
        nameFillTask = Task {
            var start = 0
            while start < files.count {
                let end = min(start + 100, files.count)
                let batch = Array(files[start..<end])
                let found = await Task.detached(priority: .userInitiated) { () -> [(MediaFile, FileMetadata)] in
                    var found: [(MediaFile, FileMetadata)] = []
                    for file in batch {
                        found.append((file, file.isVideo ? await FileMetadata.readVideo(file.url) : FileMetadata.read(file.url)))
                    }
                    return found
                }.value
                guard !Task.isCancelled, generation == self.loadGeneration else { return }
                var updated: [String: FileRow] = [:]
                for (file, metadata) in found {
                    self.fileMetadata[file.descriptionKey] = metadata
                    updated[FolderID.file(file.path)] = FileRow(file: file, metadata: metadata)
                }
                self.rows = self.rows.map { updated[$0.id] ?? $0 }
                self.nameProgress = "Reading details, \(end.formatted()) of \(files.count.formatted())…"
                start = end
            }
            self.nameProgress = nil
            self.rows.sort(using: self.sortOrder)
        }
    }

    /// Scans the added folders off the main actor and rebuilds the Folders section. With `reloadCurrent`, the
    /// folder showing is listed again even if its counts didn't change (a file was replaced).
    /// Scans the added folders (or just `root`, the one that changed) for the sidebar, off the main actor. A newer
    /// scan of a folder cancels an older one still walking it, so a stream of changes in a big folder (a download,
    /// Finder's .DS_Store) doesn't pile up scans.
    private func refreshFolders(only root: String? = nil, reloadCurrent: Bool = false) {
        let roots = root.map { [$0] } ?? folderRoots
        let scans = roots.map { root -> Task<FolderNode, Never> in
            let key = root.lowercased()
            folderScans[key]?.cancel()
            let scan = Task.detached(priority: .utility) { FolderScanner.scan(root) }
            folderScans[key] = scan
            return scan
        }
        Task {
            var fresh: [String: FolderNode] = [:]
            var nodes: [(Task<FolderNode, Never>, FolderNode)] = []
            for scan in scans { nodes.append((scan, await scan.value)) }
            // Checked once they're all in: a folder scanned again meanwhile has its older result left out.
            for (scan, node) in nodes where !scan.isCancelled {
                let key = node.path.lowercased()
                fresh[key] = node
                if folderScans[key] == scan { folderScans[key] = nil }
            }
            guard !fresh.isEmpty else { return }
            // In the order they were added; folders removed meanwhile drop out.
            let old = Dictionary(folderNodes.map { ($0.path.lowercased(), $0) }, uniquingKeysWith: { a, _ in a })
            folderNodes = folderRoots.compactMap { fresh[$0.lowercased()] ?? old[$0.lowercased()] }
            let reloaded = rebuildSidebar()
            if reloadCurrent, !reloaded, currentItem?.isDirectory == true {
                loadSelectedSource(keepSelection: true)
            }
        }
    }

    /// Watches the folder showing (or stops watching, for nil): files added, removed or renamed there refresh the
    /// list and the sidebar's counts a moment later.
    private func watchFolder(_ path: String?) {
        folderWatcher?.cancel()
        folderWatcher = nil
        guard let path else { return }
        let descriptor = Darwin.open(path, O_EVTONLY)
        guard descriptor >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: descriptor, eventMask: [.write, .rename, .delete],
                                                               queue: .main)
        source.setEventHandler { [weak self] in
            MainActor.assumeIsolated { self?.folderDidChange(path) }
        }
        source.setCancelHandler { close(descriptor) }
        source.resume()
        folderWatcher = source
    }

    private func folderDidChange(_ folder: String) {
        folderChangeTask?.cancel()
        folderChangeTask = Task {
            // Copying many files in sends many changes; wait for them to settle.
            try? await Task.sleep(for: .seconds(1))
            guard !Task.isCancelled else { return }
            // Only the added folder holding the one that changed is scanned again.
            let root = folderRoots.first { FolderRoots.same($0, folder) || FolderRoots.isInside(folder, $0) }
            refreshFolders(only: root, reloadCurrent: true)
        }
    }

    /// View > Refresh (Command-R): reads the added folders and the place showing again, as Windows' Refresh (F5).
    func refresh() {
        announceNextLoad = true
        loadSelectedSource(keepSelection: true)
        if !folderRoots.isEmpty { refreshFolders() }
    }

    /// File > Add Folder to Library…: folders from anywhere on this Mac or a connected disk, listed under Folders.
    func addFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = true
        panel.prompt = "Add"
        panel.message = "Choose a folder to add to \(AppInfo.name)'s library."
        guard panel.runModal() == .OK, !panel.urls.isEmpty else { return }
        addFolders(panel.urls.map(\.path))
    }

    /// Adds folders to the library and says what happened, as Windows does ("Added Pictures").
    func addFolders(_ paths: [String]) {
        var roots = folderRoots
        var added: [String] = [], already: [String] = []
        for path in paths {
            let result = FolderRoots.adding(path, to: roots)
            roots = result.roots
            let name = FolderRoots.displayName(FolderRoots.normalize(path))
            if result.added { added.append(name) } else { already.append(name) }
        }
        folderRoots = roots
        FolderRoots.save(roots)
        refreshFolders()
        if added.isEmpty {
            announce(already.count == 1 ? "\(already[0]) is already in the library" : "Those folders are already in the library")
        } else {
            announce(added.count == 1 ? "Added \(added[0])" : "Added \(added.count) folders")
        }
    }

    /// The added folder the place showing is in, if it's a folder: what Remove Folder from Library removes.
    var currentFolderRoot: String? {
        currentItem?.directoryPath.flatMap { FolderRoots.root(containing: $0, in: folderRoots) }
    }

    /// File > Remove Folder from Library: takes the added folder you're in out of the library, after asking.
    /// Nothing on disk changes.
    func removeCurrentFolder() {
        guard let root = currentFolderRoot else {
            NSSound.beep()
            announce("Only folders in the library can be removed.")
            return
        }
        let name = FolderRoots.displayName(root)
        let alert = NSAlert()
        alert.messageText = "Remove “\(name)” from \(AppInfo.name)?"
        alert.informativeText = "The folder and its photos stay on your Mac; \(AppInfo.name) just stops listing them.\n\n\(root)"
        alert.addButton(withTitle: "Remove")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        folderRoots = FolderRoots.removing(root, from: folderRoots)
        FolderRoots.save(folderRoots)
        folderNodes.removeAll { FolderRoots.same($0.path, root) }
        // You're always in the folder being removed: go to All Photos first, so its place going away isn't announced
        // as if it happened by itself. Said as on Windows: "Removed Pictures from Photo Finder. Showing All Photos, 12 items".
        loadAnnouncementPrefix = "Removed \(name) from \(AppInfo.name). Showing "
        selectedSourceID = SidebarID.allPhotos
        rebuildSidebar()
        refreshFolders()
    }

    /// Delete in the sidebar, as on Windows: removes an added folder (after asking). A folder inside one stays with it.
    func deleteInSidebar() {
        guard let item = currentItem, let path = item.directoryPath, let root = currentFolderRoot else {
            NSSound.beep()
            announce("Only folders in the library can be removed.")
            return
        }
        guard FolderRoots.same(path, root) else {
            NSSound.beep()
            announce("This folder is part of \(FolderRoots.displayName(root)). Only whole library folders can be removed.")
            return
        }
        removeCurrentFolder()
    }

    private func libraryDidChange(_ change: PHChange) {
        if let all = allPhotosFetch, let details = change.changeDetails(for: all) {
            allPhotosFetch = details.fetchResultAfterChanges
            // Without incremental changes Photos doesn't say what's new; those photos are described in Get Info as usual.
            if describeInBackground, details.hasIncrementalChanges, !details.insertedObjects.isEmpty {
                describeLater(details.insertedObjects)
            }
        }

        if let fetch = currentFetch, let details = change.changeDetails(for: fetch) {
            currentFetch = details.fetchResultAfterChanges
            if details.hasIncrementalChanges, details.insertedObjects.isEmpty, details.removedObjects.isEmpty {
                for asset in details.changedObjects {
                    assetsByID[asset.localIdentifier] = asset
                    if let index = rows.firstIndex(where: { $0.id == asset.localIdentifier }) {
                        rows[index] = FileRow(asset: asset, name: nil).withDetails(AssetDetails.load(for: asset, names: names))
                    }
                }
            } else {
                loadSelectedSource(keepSelection: true)
            }
        }

        refreshTask?.cancel()
        refreshTask = Task {
            try? await Task.sleep(for: .seconds(1))
            guard !Task.isCancelled else { return }
            let oldCount = allPhotosCount
            rebuildSidebar()
            if allPhotosCount != oldCount {
                await refreshDateBuckets()
                rebuildSidebar()
            }
        }
    }

    // MARK: - Navigation

    func navigate(to id: String, selecting childID: String? = nil) {
        guard itemsByID[id] != nil else { return }
        var parent = itemsByID[id]?.parentID
        while let p = parent {
            expandedIDs.insert(p)
            parent = itemsByID[p]?.parentID
        }
        pendingSelection = childID
        announceNextLoad = true
        if selectedSourceID == id {
            loadSelectedSource(keepSelection: false)
        } else {
            selectedSourceID = id
        }
    }

    func goToEnclosingFolder() {
        guard let item = currentItem, let parentID = item.parentID else {
            NSSound.beep()
            return
        }
        navigate(to: parentID, selecting: item.id)
    }

    // MARK: - Actions

    /// Selected photos in the order they are listed.
    private func assets(for ids: Set<String>) -> [PHAsset] {
        rows.filter { ids.contains($0.id) }.compactMap { assetsByID[$0.id] }
    }

    /// Selected files from an added folder, in the order they are listed (#36).
    private func files(for ids: Set<String>) -> [MediaFile] {
        rows.filter { ids.contains($0.id) }.compactMap { filesByID[$0.id] }
    }

    func open(_ ids: Set<String>? = nil) {
        let ids = ids ?? selection
        let chosen = rows.filter { ids.contains($0.id) }
        if let container = chosen.first(where: \.isContainer), chosen.allSatisfy(\.isContainer) {
            navigate(to: container.id)
            return
        }
        // Files in an added folder open as they are; Photos' photos are exported first.
        let files = files(for: ids)
        if !files.isEmpty {
            openInApps(files.map(\.url))
            return
        }
        let assets = assets(for: ids)
        guard !assets.isEmpty else { NSSound.beep(); return }
        perform("Opening…") {
            self.openInApps(try await self.exportTemporary(assets))
        }
    }

    /// Opens files with the app each opens in, one launch per app.
    private func openInApps(_ urls: [URL]) {
        let workspace = NSWorkspace.shared
        var byApp: [URL: [URL]] = [:]
        for url in urls {
            if let app = workspace.urlForApplication(toOpen: url) { byApp[app, default: []].append(url) }
        }
        for (app, files) in byApp {
            workspace.open(files, withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration(), completionHandler: nil)
        }
    }

    func quickLook(_ ids: Set<String>? = nil) {
        if previewURL != nil {
            previewURL = nil
            return
        }
        let files = Array(files(for: ids ?? selection).prefix(200))
        if !files.isEmpty {
            previewURLs = files.map(\.url)
            previewURL = previewURLs.first
            return
        }
        let assets = Array(assets(for: ids ?? selection).prefix(200))
        guard !assets.isEmpty else { NSSound.beep(); return }
        perform("Preparing preview…") {
            let urls = try await self.exportTemporary(assets)
            self.previewURLs = urls
            self.previewURL = urls.first
        }
    }

    func exportSelection(_ ids: Set<String>? = nil) {
        let assets = assets(for: ids ?? selection)
        let files = files(for: ids ?? selection)
        let count = assets.count + files.count
        guard count > 0 else { NSSound.beep(); return }
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Export"
        panel.message = count == 1 ? "Choose a folder to export 1 item to." : "Choose a folder to export \(count) items to."
        guard panel.runModal() == .OK, let folder = panel.url else { return }
        perform("Exporting…") {
            var done = 0
            for asset in assets {
                self.activity = "Exporting \(done + 1) of \(count)…"
                _ = try await PhotoExporter.export(asset, into: folder)
                done += 1
            }
            // A file is copied as it is, as Windows does.
            for file in files {
                self.activity = "Exporting \(done + 1) of \(count)…"
                let source = file.url
                _ = try await Task.detached(priority: .userInitiated) { try PhotoExporter.copyFile(source, into: folder) }.value
                done += 1
            }
            self.announce("Exported \(done == 1 ? "1 item" : "\(done) items") to \(folder.lastPathComponent)")
        }
    }

    func copySelection(_ ids: Set<String>? = nil) {
        let files = files(for: ids ?? selection)
        if !files.isEmpty {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.writeObjects(files.map { $0.url as NSURL })
            announce(files.count == 1 ? "Copied \(files[0].name)" : "Copied \(files.count) items")
            return
        }
        let assets = assets(for: ids ?? selection)
        guard !assets.isEmpty else { NSSound.beep(); return }
        perform("Copying…") {
            let urls = try await self.exportTemporary(assets)
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            pasteboard.writeObjects(urls as [NSURL])
            self.announce(urls.count == 1 ? "Copied \(urls[0].lastPathComponent)" : "Copied \(urls.count) items")
        }
    }

    func toggleFavorite(_ ids: Set<String>? = nil) {
        let assets = assets(for: ids ?? selection)
        guard !assets.isEmpty else {
            // Windows makes a file a favorite by rating it five stars; writing file metadata on the Mac is to come.
            if !files(for: ids ?? selection).isEmpty { announce("Favorites aren't available for files yet") } else { NSSound.beep() }
            return
        }
        let makeFavorite = !assets.allSatisfy(\.isFavorite)
        let box = UncheckedBox(value: assets)
        perform("Updating favorites…") {
            try await PHPhotoLibrary.shared().performChanges {
                for asset in box.value {
                    PHAssetChangeRequest(for: asset).isFavorite = makeFavorite
                }
            }
            self.announce(makeFavorite ? "Added to Favorites" : "Removed from Favorites")
        }
    }

    func showInfo(_ ids: Set<String>? = nil) {
        let ids = ids ?? selection
        guard let row = rows.first(where: { ids.contains($0.id) && !$0.isContainer }) else {
            NSSound.beep()
            return
        }
        if let asset = assetsByID[row.id] {
            infoTarget = InfoTarget(id: row.id, source: .asset(asset), name: row.name)
        } else if let file = filesByID[row.id] {
            infoTarget = InfoTarget(id: row.id, source: .file(file, row), name: row.name)
        } else {
            NSSound.beep()
        }
    }

    private func exportTemporary(_ assets: [PHAsset]) async throws -> [URL] {
        var urls: [URL] = []
        for (index, asset) in assets.enumerated() {
            if assets.count > 1 { activity = "Preparing \(index + 1) of \(assets.count)…" }
            urls.append(try await PhotoExporter.exportToTemporary(asset))
        }
        return urls
    }

    private func perform(_ label: String, _ work: @escaping () async throws -> Void) {
        Task {
            activity = label
            do {
                try await work()
            } catch {
                errorMessage = error.localizedDescription
            }
            activity = nil
        }
    }

    // MARK: - Captions

    var canEditCaptions: Bool {
        if #available(macOS 27, *) { return true }
        return false
    }

    func editCaption(_ ids: Set<String>? = nil) {
        let ids = ids ?? selection
        // Captions are written to Photos; writing them into files in added folders is to come (#36).
        if !files(for: ids).isEmpty, assets(for: ids).isEmpty {
            announce("Editing captions isn't available for files yet")
            return
        }
        guard canEditCaptions else {
            errorMessage = "Editing captions requires macOS 27 or later."
            return
        }
        let chosen = rows.filter { ids.contains($0.id) && !$0.isContainer && assetsByID[$0.id] != nil }
        guard !chosen.isEmpty else { NSSound.beep(); return }
        guard chosen.count == 1, let asset = assetsByID[chosen[0].id] else {
            captionTarget = CaptionTarget(assetIDs: chosen.map(\.id), initialText: "")
            return
        }
        // The caption as Photos has it, not the list's copy: that's empty until the list's details load, and saving
        // an empty field would remove the real caption.
        Task {
            let caption = await currentCaptions(of: [asset])[asset.localIdentifier] ?? chosen[0].caption
            // Pressed twice: the sheet from the first press is already up, with whatever's been typed in it.
            guard captionTarget == nil else { return }
            captionTarget = CaptionTarget(assetIDs: [chosen[0].id], initialText: caption)
        }
    }

    func saveCaption(_ text: String, for target: CaptionTarget) {
        let assets = target.assetIDs.compactMap { assetsByID[$0] }
        guard !assets.isEmpty else { return }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        writeCaptions(assets.map { ($0, trimmed) }, label: "Saving caption…",
                      done: trimmed.isEmpty ? "Caption removed" : "Caption saved")
    }

    /// Writes each photo's caption (an empty one removes it), then says `done` if given.
    private func writeCaptions(_ captions: [(PHAsset, String)], label: String, done: String?) {
        let box = UncheckedBox(value: captions)
        perform(label) {
            try await PHPhotoLibrary.shared().performChanges {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
                if #available(macOS 27, *) {
                    for (asset, caption) in box.value {
                        PHAssetChangeRequest(for: asset).caption = caption.isEmpty ? nil : caption
                    }
                }
#endif
            }
            if let done { self.announce(done) }
        }
    }

    // MARK: - Descriptions as captions (#30)

    /// Settings > Save descriptions as captions.
    var captionPolicy: CaptionPolicy { CaptionPolicy.current }

    /// Photo > Use Description as Caption: each selected photo's saved description becomes its caption. Asks before
    /// replacing captions people wrote.
    func useDescriptionAsCaption(_ ids: Set<String>? = nil) {
        if !files(for: ids ?? selection).isEmpty, assets(for: ids ?? selection).isEmpty {
            announce("Using a description as the caption isn't available for files yet")
            return
        }
        guard canEditCaptions else {
            errorMessage = "Editing captions requires macOS 27 or later."
            return
        }
        let ids = ids ?? selection
        let chosen = rows.filter { ids.contains($0.id) && !$0.isContainer }
        guard !chosen.isEmpty else { announce("Select a photo"); return }
        let described = chosen.compactMap { row -> (FileRow, PHAsset, String)? in
            guard let asset = assetsByID[row.id],
                  let text = descriptions.description(for: asset)?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !text.isEmpty else { return nil }
            return (row, asset, text)
        }
        guard !described.isEmpty else {
            announce(chosen.count == 1 ? "\(chosen[0].name) hasn't been described yet. Describe it first."
                     : "None of these photos have been described yet.")
            return
        }
        let skipped = chosen.count - described.count
        Task {
            // The captions as Photos has them now, not the list's copy, which is empty until its details load.
            let existing = await currentCaptions(of: described.map(\.1))
            let replacing = described.compactMap { row, asset, text -> (String, String)? in
                let caption = (existing[asset.localIdentifier] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                return caption.isEmpty || caption == text ? nil : (row.name, caption)
            }
            if !replacing.isEmpty {
                let alert = NSAlert()
                alert.messageText = replacing.count == 1 ? "Replace the caption of \(replacing[0].0)?"
                    : "Replace the captions of \(replacing.count) photos?"
                alert.informativeText = replacing.count == 1 ? "Its caption is: \(replacing[0].1)"
                    : "Their captions will be replaced with their descriptions."
                alert.addButton(withTitle: "Replace")
                alert.addButton(withTitle: "Cancel")
                guard alert.runModal() == .alertFirstButtonReturn else { return }
            }
            var done = described.count == 1 ? "Caption saved" : "Captions saved for \(described.count) photos"
            if skipped > 0 { done += ". \(skipped == 1 ? "1 photo hasn't" : "\(skipped) photos haven't") been described yet" }
            writeCaptions(described.map { ($0.1, $0.2) }, label: "Saving caption…", done: done)
        }
    }

    /// Each photo's caption as Photos has it right now, by local identifier, read off the main actor.
    private func currentCaptions(of assets: [PHAsset]) async -> [String: String] {
        let ids = assets.map(\.localIdentifier)
        let names = self.names
        return await Task.detached(priority: .userInitiated) {
            // Fetched again so it's the latest version, not one this window has held since the list loaded.
            let fetch = PHAsset.fetchAssets(withLocalIdentifiers: ids, options: RowBuilder.fetchOptions())
            var captions: [String: String] = [:]
            fetch.enumerateObjects { asset, _, _ in
                captions[asset.localIdentifier] = AssetDetails.load(for: asset, names: names).caption
            }
            return captions
        }.value
    }

    /// A description was just saved: with Save descriptions as captions on, it goes into the caption too, quietly.
    private func captionFromNewDescription(_ text: String, key: String) {
        let policy = captionPolicy
        // Files in added folders keep their captions until writing file metadata comes (#31).
        guard policy != .never, canEditCaptions, !key.hasPrefix("file:") else { return }
        let names = self.names
        Task.detached(priority: .utility) {
            // The caption as Photos has it now: never the list's copy, which is empty until its details load, so
            // "Only When There's No Caption" can't mistake an unloaded caption for none.
            guard let asset = PHAsset.fetchAssets(withLocalIdentifiers: [key], options: RowBuilder.fetchOptions()).firstObject
            else { return }
            let existing = AssetDetails.load(for: asset, names: names).caption
            guard policy.shouldWrite(text, over: existing) else { return }
            await Self.writeCaptionQuietly(UncheckedBox(value: asset), text.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    /// Writes a caption without the status bar or an error dialog: automatic captions can come one after another
    /// while describing in the background, and one dialog each would bury the person. Failures are logged.
    nonisolated private static func writeCaptionQuietly(_ asset: UncheckedBox<PHAsset>, _ caption: String) async {
        do {
            try await PHPhotoLibrary.shared().performChanges {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
                if #available(macOS 27, *) {
                    PHAssetChangeRequest(for: asset.value).caption = caption
                }
#endif
            }
        } catch {
            log.error("Couldn't save a description as a caption: \(error.localizedDescription, privacy: .public)")
        }
    }

    // MARK: - Describing in the background

    /// Photo > Describe New Photos in the Background.
    func setDescribeInBackground(_ on: Bool) {
        describeInBackground = on
        allPhotosFetch = on ? PHAsset.fetchAssets(with: .image, options: nil) : nil
        guard on else {
            let left = stopBackgroundDescribing(announce: false)
            announce(left > 0 ? "Background describing off. Stopped with \(left == 1 ? "1 photo" : "\(left) photos") not described."
                     : "New photos won't be described in the background")
            return
        }
        if let reason = backgroundUnavailableReason {
            announce("Photos can't be described in the background. \(reason)")
        } else {
            announce("Photos added to your library will be described in the background")
        }
    }

    /// Why background describing can't work on this Mac, or nil when it can.
    var backgroundUnavailableReason: String? {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) { return ImageConversation.unavailableReason }
#endif
        return "Describing photos needs Apple Intelligence on macOS 27 or later."
    }

    var isDescribingInBackground: Bool { backgroundProgress != nil }

    /// Photo > Stop Background Describing: stops this run; the setting stays on for photos added later.
    @discardableResult
    func stopBackgroundDescribing(announce shouldAnnounce: Bool = true) -> Int {
        var left = 0
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *), let describer = backgroundDescriber as? BackgroundDescriber {
            left = describer.stop()
        }
#endif
        if shouldAnnounce {
            announce(left > 0 ? "Stopped describing in the background. \(left == 1 ? "1 photo" : "\(left) photos") not described."
                     : "Nothing is being described in the background")
        }
        return left
    }

    private func describeLater(_ assets: [PHAsset]) {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            let describer = (backgroundDescriber as? BackgroundDescriber) ?? BackgroundDescriber(cache: descriptions) { [weak self] text in
                self?.backgroundProgress = text
            }
            backgroundDescriber = describer
            describer.enqueue(assets)
        }
#endif
    }

    // MARK: - Describe Photo

    /// Photo > Describe Photo (Shift-Command-D): describes the photo you're on with Apple Intelligence and speaks it,
    /// in the list or gallery. A saved description is spoken at once.
    func describeFocusedPhoto() {
        guard let row = focusedRow, !row.isContainer, let key = descriptionKey(for: row.id) else {
            announce("Select a photo to describe")
            return
        }
        guard isPhoto(row.id) else {
            announce("Videos can't be described")
            return
        }
        if let saved = descriptions.description(forKey: key) {
            announce(saved)
            return
        }
        if let reason = describeUnavailableReason {
            announce(reason)
            return
        }
        // Already being described automatically: its description is spoken when it's ready.
        if autoDescriber.describingID == row.id {
            announce("Describing \(row.name)")
            return
        }
        autoDescriber.describingManually(row.id)
        describeTask?.cancel()
        announce("Describing \(row.name)")
        let id = row.id
        describeTask = Task { [weak self] in
            guard let text = await self?.describeAndRemember(id, allowNetwork: true) else { return }
            self?.say(text, ifStillOn: id)
        }
    }

    /// Photo > Describe Again (Shift-Option-Command-D): describes the photo you're on afresh, replaces its saved
    /// description, and speaks the new one. For a saved description that's poor, or from before the photo was edited
    /// or the prompt changed.
    func describeFocusedPhotoAgain() {
        guard let row = focusedRow, !row.isContainer, let key = descriptionKey(for: row.id) else {
            announce("Select a photo to describe")
            return
        }
        guard isPhoto(row.id) else {
            announce("Videos can't be described")
            return
        }
        if let reason = describeUnavailableReason {
            announce(reason)
            return
        }
        // A description the person corrected is theirs: ask before Apple Intelligence replaces it. Only the
        // correction agreed to is replaced; one typed after this keeps.
        let agreed = descriptions.isEdited(forKey: key) ? descriptions.description(forKey: key) : nil
        if agreed != nil {
            let alert = NSAlert()
            alert.messageText = "Replace the description you edited?"
            alert.informativeText = "\(row.name) will be described again, and the new description replaces yours."
            alert.addButton(withTitle: "Describe Again")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        let id = row.id
        autoDescriber.describingManually(id)
        describingAgain[id, default: 0] += 1
        describeTask?.cancel()
        announce("Describing \(row.name) again")
        describeTask = Task { [weak self] in
            let text = await self?.describeAndRemember(id, allowNetwork: true, replacingEdit: agreed)
            self?.describingAgain[id, default: 1] -= 1
            guard let text else { return }
            self?.say(text, ifStillOn: id)
        }
    }

    // MARK: - Description panel and column (#50)

    /// Whether a row is a photo that can have a description: from Photos or a folder, not a video.
    func isDescribable(_ id: String) -> Bool { isPhoto(id) }

    /// What the Description panel says about a photo's description.
    func descriptionStatus(for id: String) -> String {
        _ = descriptionRevision
        if describingIDs[id, default: 0] > 0 { return "Describing…" }
        guard let key = descriptionKey(for: id), descriptions.description(forKey: key) != nil else { return "Not described yet" }
        return descriptions.isEdited(forKey: key) ? "Edited by you" : "Saved description"
    }

    /// The Description panel's text box: the person's own description or correction replaces the saved one, marked
    /// as edited so it isn't replaced without asking.
    func saveEditedDescription(_ text: String, for id: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let key = descriptionKey(for: id) else { return }
        guard !trimmed.isEmpty else {
            announce("A description can't be empty. The saved one is kept.")
            return
        }
        guard trimmed != descriptions.description(forKey: key) else { return }
        descriptions.remember(trimmed, forKey: key, edited: true)
        announce("Description saved")
    }

    /// The Description panel's Copy.
    func copyDescription(for id: String) {
        guard let text = savedDescription(for: id) else { NSSound.beep(); return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        announce("Description copied")
    }

    /// Whether rows need their descriptions: the Description column is showing, or the list is sorted by it (Sort By
    /// lists every column).
    private var showsDescriptionColumn: Bool { columnOrder.contains("description") || sortColumnID == "description" }

    /// Copies saved descriptions into the rows for the Description column, and re-sorts if that's the sort.
    private func fillDescriptions() {
        fillingDescriptions = true
        defer { fillingDescriptions = false }
        var filled = rows.map { row in row.withDescription(descriptionKey(for: row.id).flatMap(descriptions.description(forKey:)) ?? "") }
        if sortColumnID == "description" { filled.sort(using: sortOrder) }
        if filled.map(\.descriptionText) != rows.map(\.descriptionText) { rows = filled }
    }

    // MARK: - Help

    /// Help > Check System chosen; an open Check System window checks again.
    var systemCheckRequest = 0

    /// Help > Check System.
    func systemCheck() -> SystemCheck {
#if arch(arm64)
        let appleSilicon = true
#else
        let appleSilicon = false
#endif
        return SystemCheck(.init(
            appName: AppInfo.name, appVersion: AppInfo.version,
            osVersion: ProcessInfo.processInfo.operatingSystemVersion, appleSilicon: appleSilicon,
            aiUnavailableReason: describeUnavailableReason,
            photosAccess: PHPhotoLibrary.authorizationStatus(for: .readWrite),
            savedDescriptions: descriptions.count,
            describeAutomatically: describeAutomatically, describeInBackground: describeInBackground))
    }

    /// Help > Report a Bug…: a new GitHub issue with the system check filled in, and the same text on the clipboard
    /// in case the page doesn't take it all. The app kit's relay replaces this once it's set up for the app (#33).
    func reportBug() {
        let body = SystemCheck.bugReportTemplate(systemText: systemCheck().text)
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(body, forType: .string)
        var components = URLComponents(url: AppInfo.issuesURL, resolvingAgainstBaseURL: false)!
        // GitHub only applies labels for people who can triage the repo, so the title says it's from the Mac too.
        components.queryItems = [URLQueryItem(name: "title", value: "[Mac] "), URLQueryItem(name: "body", value: body),
                                 URLQueryItem(name: "labels", value: "bug,user-reported,mac")]
        if let url = components.url { NSWorkspace.shared.open(url) }
        announce("Opening a new bug report in your browser. The details are also on the clipboard.")
    }

    /// Why photos can't be described on this Mac right now, or nil when they can. Never sets anything up.
    private var describeUnavailableReason: String? {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) { return ImageConversation.unavailableReason }
#endif
        return "Describing photos needs Apple Intelligence on macOS 27 or later."
    }

    /// Describes the photo with Apple Intelligence and saves the description. Returns what to say: the description,
    /// or why there isn't one; nil for a row that isn't a photo. Nil too, saying nothing, when the photo is only in
    /// iCloud and `allowNetwork` is false, or when a newer request replaced this one. A file in an added folder is
    /// read from disk and saved under its file key (#36).
    /// `replacingEdit` is the correction the person agreed Describe Again may replace; any other correction (one made
    /// meanwhile in the Description panel) is kept.
    private func describeAndRemember(_ id: String, allowNetwork: Bool, replacingEdit: String? = nil,
                                     keep: @MainActor () -> Bool = { true }) async -> String? {
        describingIDs[id, default: 0] += 1
        defer {
            describingIDs[id, default: 1] -= 1
            if describingIDs[id] == 0 { describingIDs[id] = nil }
        }
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            let key: String
            let data: Data?
            if let asset = assetsByID[id] {
                key = asset.localIdentifier
                let box = UncheckedBox(value: asset)
                data = await PhotoData.request(box.value, allowNetwork: allowNetwork)?.data
                if data == nil, !allowNetwork { return nil }
            } else if let file = filesByID[id] {
                key = file.descriptionKey
                let url = file.url
                data = await Task.detached(priority: .userInitiated) { try? Data(contentsOf: url, options: .mappedIfSafe) }.value
            } else {
                return nil
            }
            guard let data else { return "This photo couldn't be loaded" }
            do {
                guard let text = try await PhotoDescriber.describe(data), !text.isEmpty else {
                    return "Apple Intelligence returned an empty description"
                }
                // A newer Describe Photo replaced this one: its description is the one to keep.
                guard !Task.isCancelled, keep() else { return nil }
                // Corrected in the Description panel while this was being described: the person's words win.
                guard DescriptionCache.mayReplace(edited: descriptions.isEdited(forKey: key),
                                                  saved: descriptions.description(forKey: key), agreed: replacingEdit)
                else { return nil }
                descriptions.remember(text, forKey: key)
                return text
            } catch {
                return ImageConversation.message(for: error)
            }
        }
#endif
        guard descriptionKey(for: id) != nil else { return nil }
        return "Describing photos needs Apple Intelligence on macOS 27 or later."
    }

    /// Spoken only if you're still on that photo and haven't asked about another since.
    private func say(_ text: String, ifStillOn id: String) {
        guard !Task.isCancelled, focusedRow?.id == id else { return }
        autoDescriber.spoken(id)
        announce(text)
    }


    // MARK: - Gallery

    /// The item the gallery shows: the first selected row in list order.
    var focusedRow: FileRow? {
        guard !selection.isEmpty else { return nil }
        return visibleRows.first { selection.contains($0.id) }
    }

    var galleryPosition: (index: Int, count: Int)? {
        let rows = visibleRows
        guard let index = rows.firstIndex(where: { selection.contains($0.id) }) else { return nil }
        return (index, rows.count)
    }

    func ensureGallerySelection() {
        guard focusedRow == nil, let first = visibleRows.first else { return }
        selection = [first.id]
        cacheNeighbors(of: 0, in: visibleRows)
    }

    /// Home and End in the gallery: the first or last item, whatever's selected now (even a photo Search hides).
    func moveSelectionToEnd(last: Bool) {
        let rows = visibleRows
        guard !rows.isEmpty else { NSSound.beep(); return }
        select(at: last ? rows.count - 1 : 0, in: rows)
    }

    func moveSelection(by delta: Int) {
        let rows = visibleRows
        guard !rows.isEmpty else { NSSound.beep(); return }
        let current = rows.firstIndex { selection.contains($0.id) }
        let next = current.map { $0 + delta } ?? 0
        guard rows.indices.contains(next) else { NSSound.beep(); return }
        select(at: next, in: rows)
    }

    private func select(at index: Int, in rows: [FileRow]) {
        selection = [rows[index].id]
        cacheNeighbors(of: index, in: rows)
        announce("\(rows[index].name), \(index + 1) of \(rows.count)")
    }

    /// Starts loading the images on either side so moving through the gallery feels instant.
    private func cacheNeighbors(of index: Int, in rows: [FileRow]) {
        let neighbors = [index - 1, index + 1, index + 2]
            .filter { rows.indices.contains($0) }
            .compactMap { assetsByID[rows[$0].id] }
        guard !neighbors.isEmpty else { return }
        imageManager.startCachingImages(for: neighbors, targetSize: galleryImageSize,
                                        contentMode: .aspectFit, options: galleryImageOptions)
    }

    func fullImage(for id: String) async -> NSImage? {
        if let file = filesByID[id] { return await FileImages.image(file.url, maxPixels: Int(galleryImageSize.width)) }
        guard let asset = assetsByID[id] else { return nil }
        let box = UncheckedBox(value: asset)
        return await withCheckedContinuation { continuation in
            imageManager.requestImage(for: box.value, targetSize: galleryImageSize, contentMode: .aspectFit,
                                      options: galleryImageOptions) { image, _ in
                continuation.resume(returning: image)
            }
        }
    }

    // MARK: - Thumbnails and drag

    func thumbnail(for id: String) async -> NSImage? {
        if let file = filesByID[id] { return await FileImages.thumbnail(file.url, size: 64) }
        guard let asset = assetsByID[id] else { return nil }
        let options = PHImageRequestOptions()
        options.deliveryMode = .highQualityFormat
        options.resizeMode = .fast
        options.isNetworkAccessAllowed = false
        let box = UncheckedBox(value: asset)
        return await withCheckedContinuation { continuation in
            imageManager.requestImage(for: box.value, targetSize: CGSize(width: 64, height: 64),
                                      contentMode: .aspectFill, options: options) { image, _ in
                continuation.resume(returning: image)
            }
        }
    }

    func itemProvider(for row: FileRow) -> NSItemProvider? {
        // A file in an added folder is dragged as itself.
        if let file = filesByID[row.id] { return NSItemProvider(contentsOf: file.url) }
        guard !row.isContainer, let asset = assetsByID[row.id] else { return nil }
        let provider = NSItemProvider()
        provider.suggestedName = (row.name as NSString).deletingPathExtension
        let box = UncheckedBox(value: asset)
        provider.registerFileRepresentation(forTypeIdentifier: PhotoExporter.uniformType(for: asset).identifier,
                                            fileOptions: [], visibility: .all) { completion in
            Task {
                do {
                    completion(try await PhotoExporter.exportToTemporary(box.value), false, nil)
                } catch {
                    completion(nil, false, error)
                }
            }
            return nil
        }
        return provider
    }

    func movePane(by direction: Int) {
        paneRequest = PaneRequest(direction: direction, serial: paneRequest.serial + 1)
    }

    private func announce(_ message: String, queued: Bool = false) {
        guard queued else {
            AccessibilityNotification.Announcement(message).post()
            return
        }
        // Low priority waits for VoiceOver to finish what it's saying (the row you moved to) instead of cutting it off.
        var text = AttributedString(message)
        text.accessibilitySpeechAnnouncementPriority = .low
        AccessibilityNotification.Announcement(text).post()
    }
}

final class LibraryObserver: NSObject, PHPhotoLibraryChangeObserver, @unchecked Sendable {
    private let handler: @Sendable (UncheckedBox<PHChange>) -> Void

    init(handler: @escaping @Sendable (UncheckedBox<PHChange>) -> Void) {
        self.handler = handler
    }

    func photoLibraryDidChange(_ changeInstance: PHChange) {
        handler(UncheckedBox(value: changeInstance))
    }
}
