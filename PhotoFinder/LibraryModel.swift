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
    let id: String
    let asset: PHAsset
    let name: String
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

    private(set) var rows: [FileRow] = []
    var sortOrder: [KeyPathComparator<FileRow>] = [KeyPathComparator(\FileRow.sortDate, order: .reverse)] {
        didSet { rows.sort(using: sortOrder) }
    }
    var selection: Set<String> = [] {
        didSet { if selection != oldValue { autoDescriber.moved(to: autoDescribeCandidate) } }
    }
    var searchText = ""
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
        didSet { UserDefaults.standard.set(columnOrder, forKey: "columnOrder") }
    }
    var columnsSheetShown = false {
        didSet { if !columnsSheetShown, oldValue { autoDescriber.refocused(currentID: autoDescribeCandidate) } }
    }
    /// What describing new photos in the background is doing, shown in the status bar.
    var backgroundProgress: String?
    var errorMessage: String?

    @ObservationIgnored private var assetsByID: [String: PHAsset] = [:]
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
    @ObservationIgnored private var terminationObserver: NSObjectProtocol?
    @ObservationIgnored private var activationObserver: NSObjectProtocol?
    /// Photo > Describe Photos Automatically; on unless turned off.
    var describeAutomatically: Bool = UserDefaults.standard.object(forKey: "describeAutomatically") as? Bool ?? true {
        didSet { UserDefaults.standard.set(describeAutomatically, forKey: "describeAutomatically") }
    }
    @ObservationIgnored private lazy var autoDescriber = makeAutoDescriber()

    /// Describe photos added to the Photos library behind the scenes, so their descriptions are ready in Get Info.
    var describeInBackground: Bool = UserDefaults.standard.bool(forKey: "describeInBackground") {
        didSet { UserDefaults.standard.set(describeInBackground, forKey: "describeInBackground") }
    }
    private static let log = Logger(subsystem: "net.theideaplace.PhotoFinder", category: "library")
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
        guard selection.count == 1, let id = selection.first, assetsByID[id]?.mediaType == .image,
              let row = (searchText.isEmpty ? rows : visibleRows).first(where: { $0.id == id }), !row.isContainer
        else { return nil }
        return id
    }

    private func makeAutoDescriber() -> AutoDescriber {
        let describer = AutoDescriber(isEnabled: describeAutomatically)
        describer.saved = { [weak self] id in
            guard let self, let asset = self.assetsByID[id] else { return nil }
            return self.descriptions.description(for: asset)
        }
        describer.unavailableReason = { [weak self] in self?.describeUnavailableReason }
        describer.describe = { [weak self] id in
            guard let self, let asset = self.assetsByID[id] else { return nil }
            // Not downloaded from iCloud just because you arrowed past it, as on Windows; Describe Photo does.
            return await self.describeAndRemember(asset, allowNetwork: false)
        }
        describer.isStillOn = { [weak self] id in
            // Not while another app, Get Info or a sheet is in front: you've moved on from the photo for now.
            guard let self, NSApplication.shared.isActive, NSApplication.shared.keyWindow?.isSheet == false,
                  self.infoTarget == nil, self.captionTarget == nil, !self.columnsSheetShown else { return false }
            return self.autoDescribeCandidate == id
        }
        describer.speak = { [weak self] text in self?.announce(text, queued: true) }
        return describer
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
    var currentTitle: String { currentItem?.title ?? "Photo Finder" }

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

    private func rebuildSidebar() {
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

        guard newSections != sections else { return }
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
        } else if let item = currentItem, item.children != nil, item != previousItem {
            // A folder's contents changed while it was showing.
            loadSelectedSource(keepSelection: true)
        }
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
        case .collectionList, .year:
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
            isLoading = false
            return
        }

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
        if announceNextLoad || slow {
            announceNextLoad = false
            announce("\(item.title), \(rows.count == 1 ? "1 item" : "\(rows.count.formatted()) items")")
        }
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

    func open(_ ids: Set<String>? = nil) {
        let ids = ids ?? selection
        let chosen = rows.filter { ids.contains($0.id) }
        if let container = chosen.first(where: \.isContainer), chosen.allSatisfy(\.isContainer) {
            navigate(to: container.id)
            return
        }
        let assets = assets(for: ids)
        guard !assets.isEmpty else { NSSound.beep(); return }
        perform("Opening…") {
            let urls = try await self.exportTemporary(assets)
            let workspace = NSWorkspace.shared
            var byApp: [URL: [URL]] = [:]
            for url in urls {
                if let app = workspace.urlForApplication(toOpen: url) { byApp[app, default: []].append(url) }
            }
            for (app, files) in byApp {
                workspace.open(files, withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration(), completionHandler: nil)
            }
        }
    }

    func quickLook(_ ids: Set<String>? = nil) {
        if previewURL != nil {
            previewURL = nil
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
        guard !assets.isEmpty else { NSSound.beep(); return }
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Export"
        panel.message = assets.count == 1 ? "Choose a folder to export 1 item to." : "Choose a folder to export \(assets.count) items to."
        guard panel.runModal() == .OK, let folder = panel.url else { return }
        perform("Exporting…") {
            var done = 0
            for asset in assets {
                self.activity = "Exporting \(done + 1) of \(assets.count)…"
                _ = try await PhotoExporter.export(asset, into: folder)
                done += 1
            }
            self.announce("Exported \(done == 1 ? "1 item" : "\(done) items") to \(folder.lastPathComponent)")
        }
    }

    func copySelection(_ ids: Set<String>? = nil) {
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
        guard !assets.isEmpty else { NSSound.beep(); return }
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
        guard let row = rows.first(where: { ids.contains($0.id) && !$0.isContainer }),
              let asset = assetsByID[row.id] else {
            NSSound.beep()
            return
        }
        infoTarget = InfoTarget(id: row.id, asset: asset, name: row.name)
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
        guard canEditCaptions else {
            errorMessage = "Editing captions requires macOS 27 or later."
            return
        }
        let ids = ids ?? selection
        let chosen = rows.filter { ids.contains($0.id) && !$0.isContainer }
        guard !chosen.isEmpty else { NSSound.beep(); return }
        captionTarget = CaptionTarget(assetIDs: chosen.map(\.id),
                                      initialText: chosen.count == 1 ? chosen[0].caption : "")
    }

    func saveCaption(_ text: String, for target: CaptionTarget) {
        let assets = target.assetIDs.compactMap { assetsByID[$0] }
        guard !assets.isEmpty else { return }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let box = UncheckedBox(value: assets)
        perform("Saving caption…") {
            try await PHPhotoLibrary.shared().performChanges {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
                if #available(macOS 27, *) {
                    for asset in box.value {
                        PHAssetChangeRequest(for: asset).caption = trimmed.isEmpty ? nil : trimmed
                    }
                }
#endif
            }
            self.announce(trimmed.isEmpty ? "Caption removed" : "Caption saved")
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
        guard let row = focusedRow, !row.isContainer, let asset = assetsByID[row.id] else {
            announce("Select a photo to describe")
            return
        }
        guard asset.mediaType == .image else {
            announce("Videos can't be described")
            return
        }
        if let saved = descriptions.description(for: asset) {
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
            guard let text = await self?.describeAndRemember(asset, allowNetwork: true) else { return }
            self?.say(text, ifStillOn: id)
        }
    }

    /// Why photos can't be described on this Mac right now, or nil when they can. Never sets anything up.
    private var describeUnavailableReason: String? {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) { return ImageConversation.unavailableReason }
#endif
        return "Describing photos needs Apple Intelligence on macOS 27 or later."
    }

    /// Describes the photo with Apple Intelligence and saves the description. Returns what to say: the description,
    /// or why there isn't one. Nil, saying nothing, when the photo is only in iCloud and `allowNetwork` is false,
    /// or when a newer request replaced this one.
    private func describeAndRemember(_ asset: PHAsset, allowNetwork: Bool) async -> String? {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            let box = UncheckedBox(value: asset)
            guard let data = await PhotoData.request(box.value, allowNetwork: allowNetwork)?.data else {
                return allowNetwork ? "This photo couldn't be loaded" : nil
            }
            do {
                guard let text = try await PhotoDescriber.describe(data), !text.isEmpty else {
                    return "Apple Intelligence returned an empty description"
                }
                // A newer Describe Photo replaced this one: its description is the one to keep.
                guard !Task.isCancelled else { return nil }
                descriptions.remember(text, for: box.value)
                return text
            } catch {
                return ImageConversation.message(for: error)
            }
        }
#endif
        return "Describing photos needs Apple Intelligence on macOS 27 or later."
    }

    /// Spoken only if you're still on that photo and haven't asked about another since.
    private func say(_ text: String, ifStillOn id: String) {
        guard !Task.isCancelled, focusedRow?.id == id else { return }
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

    func moveSelection(by delta: Int) {
        let rows = visibleRows
        guard !rows.isEmpty else { NSSound.beep(); return }
        let current = rows.firstIndex { selection.contains($0.id) }
        let next = current.map { $0 + delta } ?? 0
        guard rows.indices.contains(next) else { NSSound.beep(); return }
        selection = [rows[next].id]
        cacheNeighbors(of: next, in: rows)
        announce("\(rows[next].name), \(next + 1) of \(rows.count)")
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
