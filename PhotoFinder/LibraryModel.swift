import AppKit
import os
import Photos
import SwiftUI

struct CaptionTarget: Identifiable {
    let id = UUID()
    /// The rows being captioned: photos in the Photos library, or files in an added folder.
    let assetIDs: [String]
    let initialText: String
    /// Files in an added folder, whose caption is written into the file itself (#31).
    var inFiles = false
}

enum ViewMode: String {
    case list, gallery
}

struct InfoTarget: Identifiable {
    /// A photo in the Photos library, or a file in an added folder (#36).
    enum Source {
        case asset(PHAsset)
        /// With its metadata when the list has already read it; Get Info reads it otherwise.
        case file(MediaFile, FileMetadata?)
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
    var showDescriptionPanel: Bool {
        didSet { defaults.set(showDescriptionPanel, forKey: "showDescriptionPanel") }
    }
    /// Photos being described right now, by row id and how many describes are running on each, so the Description
    /// panel can say "Describing…" until the last one finishes.
    private(set) var describingIDs: [String: Int] = [:]
    /// The list's sort, remembered between launches as a column and a direction, as Windows does (#46).
    var sortOrder: [KeyPathComparator<FileRow>] {
        didSet {
            rows.sort(using: sortOrder)
            ColumnOrder.saveSort(sortOrder, to: defaults)
        }
    }
    var selection: Set<String> = [] {
        didSet { if selection != oldValue, !keepingSelectionShowing { movedSelection() } }
    }
    var searchText = "" {
        didSet {
            if searchText != oldValue { announceResultsAfterPause() }
        }
    }
    /// Set while a search drops hidden rows from the selection, which isn't a move to describe.
    @ObservationIgnored private var keepingSelectionShowing = false

    /// The selected rows that are showing: what Open, Favorite, Rating, Edit Caption and the rest act on, so nothing
    /// out of sight is changed while a search hides it, whatever reloaded or changed meanwhile (#159).
    var selectedShowing: Set<String> { showing(nil) }

    /// `ids` (a context menu's rows, which can include hidden ones it got from the selection), or the selection,
    /// without any a search hides.
    func showing(_ ids: Set<String>?) -> Set<String> {
        let ids = ids ?? selection
        guard !ids.isEmpty, !searchText.trimmingCharacters(in: .whitespaces).isEmpty else { return ids }
        return ids.intersection(visibleRows.map(\.id))
    }

    /// Once typing in Search pauses, rows it hides leave the selection, as on Windows, so they don't come back when
    /// it's cleared. Not on every key: a typo that hides the photo for a moment mustn't lose it.
    private func keepSelectionShowing() {
        let showing = selectedShowing
        guard showing != selection else { return }
        keepingSelectionShowing = true
        selection = showing
        keepingSelectionShowing = false
        // Not a move to describe, but the photo you were on is no longer it, so landing on it again speaks it.
        autoDescriber.moved(to: nil)
    }
    @ObservationIgnored private var resultsAnnouncement: Task<Void, Never>?
    var searchFocusRequest = 0
    /// Go > Next Pane and Previous Pane; the browser moves focus when this changes.
    private(set) var paneRequest = PaneRequest()
    /// Whether the status bar's sections can take focus: only while F6 is taking you there, so Tab passes them by,
    /// as on Windows (#19). VoiceOver still reads them, being text.
    var statusBarReachable = false
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
            defaults.set(viewMode.rawValue, forKey: "viewMode")
            if viewMode == .gallery { ensureGallerySelection() }
        }
    }
    /// The columns shown, in the order they're shown and read. Always includes Name.
    var columnOrder: [String] = ColumnOrder.defaultOrder {
        didSet {
            defaults.set(columnOrder, forKey: "columnOrder")
            if showsDescriptionColumn, !oldValue.contains("description") { fillDescriptions() }
        }
    }
    /// What the gallery says, in order, when you move to a photo (View > Labels > Gallery, #38). Position by default.
    var galleryLabels: [GalleryItem] = GalleryLabels.defaultOrder {
        didSet { defaults.set(galleryLabels.map(\.rawValue), forKey: GalleryLabels.defaultsKey) }
    }
    var labelsSheetShown = false {
        didSet { if !labelsSheetShown, oldValue { autoDescriber.refocused(currentID: autoDescribeCandidate) } }
    }
    /// What describing in the background is doing, shown in the status bar.
    var backgroundProgress: String?
    /// Why background describing stopped by itself (too many failures in a row), shown in the status bar until it
    /// runs again (#102).
    var backgroundProblem: String?
    /// The status bar's background section: what it's doing, or why it stopped.
    var backgroundStatus: String? { backgroundProgress ?? backgroundProblem }
    var errorMessage: String?

    @ObservationIgnored private var assetsByID: [String: PHAsset] = [:]
    /// The files listed when an added folder is showing, by row id (#36). Empty for Photos' albums.
    @ObservationIgnored private var filesByID: [String: MediaFile] = [:]
    /// Folders added to the library (File > Add Folder to Library…), in the order they were added.
    private(set) var folderRoots: [String]
    /// The Folders section's tree, from the last scan of the added folders.
    @ObservationIgnored private var folderNodes: [FolderNode] = []
    /// Photos' own sidebar sections from the last full rebuild, kept so a folder scan rebuilds only Folders (#47).
    @ObservationIgnored private var photoSections: [SidebarSection] = []
    /// Every photo and video path in each added folder at its last scan, by lowercased root, so photos added since
    /// can be described in the background (#47). No entry until a folder's first scan.
    @ObservationIgnored private var folderMediaPaths: [String: Set<String>] = [:]
    /// Added folders whose first scan should describe all their photos in the background, by lowercased path.
    @ObservationIgnored private var justAddedFolders: Set<String> = []
    /// Added folders whose photos have joined the background backlog since launch, by lowercased path (#102).
    @ObservationIgnored private var folderBacklogQueued: Set<String> = []
    /// The scan of each added folder in progress, by lowercased path; a newer scan of the same folder cancels it.
    @ObservationIgnored private var folderScans: [String: Task<FolderScan, Never>] = [:]
    /// Metadata already read from files, so going back to a folder doesn't read it all again. Keyed like
    /// descriptions, so an edited file is read afresh.
    @ObservationIgnored private var fileMetadata: [String: FileMetadata] = [:]
    /// The last write into files (#31), which the next one waits for, so an automatic caption and a command never
    /// change the same file at once.
    @ObservationIgnored private var fileWrites: Task<Void, Never>?
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
    let descriptions: DescriptionCache
    /// Where settings are remembered: the app's own defaults, or a suite of their own in tests.
    @ObservationIgnored private let defaults: UserDefaults
    /// Apple Intelligence, as Describe Photo, Redescribe and describing automatically use it; nil where it can't be
    /// used (before macOS 27). A stand-in in tests, so describing can be tested on any Mac.
    @ObservationIgnored var photoDescribing: PhotoDescribing?
    /// Says something with VoiceOver; `queued` waits for what it's saying. Collected instead in tests.
    @ObservationIgnored var announcer: @MainActor (_ message: String, _ queued: Bool) -> Void = LibraryModel.postAnnouncement
    /// Every photo, watched for new ones to describe in the background.
    @ObservationIgnored private var allPhotosFetch: PHFetchResult<PHAsset>?
    /// The background describer (macOS 27), kept untyped so this class builds with older SDKs too.
    @ObservationIgnored private var backgroundDescriber: AnyObject?
    /// Describe Photo in progress; a newer one replaces it.
    @ObservationIgnored private var describeTask: Task<Void, Never>?
    /// The photo Describe Photo is working on, so Redescribe on the same photo can supersede it.
    @ObservationIgnored private var describeTaskID: String?
    /// Redescribe in progress; a newer Redescribe replaces it, but Describe Photo doesn't.
    @ObservationIgnored private var describeAgainTask: Task<Void, Never>?
    /// Photos Redescribe is describing: an automatic description of one finishing meanwhile is older, so it's
    /// neither saved nor spoken.
    @ObservationIgnored private var describingAgain: [String: Int] = [:]
    @ObservationIgnored private var terminationObserver: NSObjectProtocol?
    @ObservationIgnored private var activationObserver: NSObjectProtocol?
    @ObservationIgnored private var descriptionObserver: NSObjectProtocol?
    /// Changes whenever a description is saved, so views showing one (the gallery) update.
    private(set) var descriptionRevision = 0
    /// Photo > Describe Photos Automatically; on unless turned off.
    var describeAutomatically: Bool {
        didSet { defaults.set(describeAutomatically, forKey: "describeAutomatically") }
    }
    /// Not private, so tests can see what automatic describing would do.
    @ObservationIgnored lazy var autoDescriber = makeAutoDescriber()
    /// Whether the window is in front with no sheet over it, so a photo you're on is still being looked at. Tests
    /// can say it is, since they run without the window in front.
    @ObservationIgnored var isInFront: @MainActor () -> Bool = {
        NSApplication.shared.isActive && NSApplication.shared.keyWindow?.isSheet == false
    }

    /// Describe photos added to the Photos library behind the scenes, so their descriptions are ready in Get Info.
    var describeInBackground: Bool {
        didSet { defaults.set(describeInBackground, forKey: "describeInBackground") }
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

    /// - Parameters:
    ///   - descriptions: the saved descriptions; tests pass one in a temporary folder.
    ///   - defaults: where settings are remembered; tests pass a suite of their own.
    ///   - photoDescribing: Apple Intelligence where it can be used; tests pass a stand-in.
    init(descriptions: DescriptionCache = DescriptionCache(), defaults: UserDefaults = .standard,
         photoDescribing: PhotoDescribing? = .appleIntelligence) {
        self.descriptions = descriptions
        self.defaults = defaults
        self.photoDescribing = photoDescribing
        showDescriptionPanel = defaults.object(forKey: "showDescriptionPanel") as? Bool ?? true
        sortOrder = ColumnOrder.savedSort(defaults)
        folderRoots = FolderRoots.load(from: defaults)
        describeAutomatically = defaults.object(forKey: "describeAutomatically") as? Bool ?? true
        describeInBackground = defaults.bool(forKey: "describeInBackground")
        saveInPhotos = CaptionPolicy.isOn(in: defaults)
        lookUpPlaceNames = PlaceNames.isOn(in: defaults)
        availableUpdate = Updates.remembered(running: AppInfo.shortVersion, in: defaults)
        if let raw = defaults.string(forKey: "viewMode"), let mode = ViewMode(rawValue: raw) {
            viewMode = mode
        }
        if let saved = defaults.stringArray(forKey: "columnOrder") {
            columnOrder = ColumnOrder.clean(saved)
        } else if let data = defaults.data(forKey: "columns"),
                  let migrated = ColumnOrder.migrate(customization: data) {
            // Columns from before they could be reordered: keep which were on, in the default order.
            columnOrder = migrated
        }
        if let saved = defaults.stringArray(forKey: GalleryLabels.defaultsKey) {
            galleryLabels = GalleryLabels.clean(saved)
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
                self.recheckPhotosAccess()
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

    // MARK: - Read-only (#84)

    /// File > Read-Only Mode: on at every launch; allowing changes lasts until the app quits (Kelly, 12 Oct).
    private(set) var readOnly = true

    /// Turns Read-Only Mode off, after asking every time, or back on.
    func setReadOnly(_ on: Bool) {
        if !on {
            let alert = NSAlert()
            alert.messageText = "Let \(AppInfo.name) change your photos?"
            alert.informativeText = "\(AppInfo.name) will be able to write captions, alt text and ratings into your photos, and Save Descriptions in Photos will work. Every change is checked before it's saved, but an undiscovered bug could still damage a photo, so keep a backup of photos you care about. You can go back to Read-Only Mode at any time."
            alert.window.title = "Allow Changes to Photos"
            // Keep Read-Only first, so it's the default: Return alone never allows changes.
            alert.addButton(withTitle: "Keep Read-Only Mode")
            alert.addButton(withTitle: "Allow Changes")
            let escape = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
                guard event.keyCode == 53 else { return event }
                NSApp.stopModal(withCode: .alertFirstButtonReturn)
                return nil
            }
            defer { if let escape { NSEvent.removeMonitor(escape) } }
            guard alert.runModal() == .alertSecondButtonReturn else { return }
        }
        readOnly = on
        PhotoChanges.allowed = !on
        announce(on ? "Read-Only Mode. Photos won't be changed" : "Editing Mode. Photos can be changed")
    }

    /// Whether a command may change photos; says why not while read-only.
    private func changesAllowed() -> Bool {
        guard readOnly else { return true }
        announce(PhotoChanges.readOnlyMessage)
        return false
    }

    /// The window's title, with the mode: "All Photos - My Photo Library Described - Read-Only Mode". "Mode", so a screen reader
    /// doesn't sound as if the window or a control is read-only (Kelly, 13 Oct).
    var windowTitle: String { "\(currentTitle) - \(AppInfo.name) - \(readOnly ? "Read-Only Mode" : "Editing Mode")" }

    /// View > Read Status Bar (Control-F9): says what the status bar says, without moving.
    func readStatusBar() {
        let parts = [statusText, activity ?? nameProgress, backgroundStatus].compactMap { $0 }.filter { !$0.isEmpty }
        announce(parts.isEmpty ? "Status bar is empty" : parts.joined(separator: ". "))
    }

    /// View > Go to Status Bar (Control-Shift-F9).
    func goToStatusBar() {
        paneRequest = PaneRequest(direction: 0, serial: paneRequest.serial + 1, target: .status(.status))
    }

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

    /// The photo you were on and where it was in the list, to tell a step to the next or previous photo from a jump.
    @ObservationIgnored private var lastPhoto: (id: String, index: Int)?

    private func movedSelection() {
        let id = autoDescribeCandidate
        let list = searchText.isEmpty ? rows : visibleRows
        let index = id.flatMap { id in list.firstIndex { $0.id == id } }
        var step = 0
        // Only if that photo is still where it was: after a re-sort, reload or search, a ±1 would be a coincidence.
        // Rows updated in place (a caption written, details read) leave it where it was, so stepping carries on.
        if let index, let last = lastPhoto, list.indices.contains(last.index), list[last.index].id == last.id {
            step = index - last.index
        }
        lastPhoto = index.map { (id!, $0) }
        autoDescriber.moved(to: id, step: step)
    }

    /// The photo to describe ahead of `id`, going `step` (1 or -1): the very next row, if it's a photo with no
    /// description yet. Not past a video or folder: landing on that drops any work ahead anyway.
    private func photoAhead(of id: String, step: Int) -> String? {
        let list = searchText.isEmpty ? rows : visibleRows
        guard let index = list.firstIndex(where: { $0.id == id }), list.indices.contains(index + step) else { return nil }
        let row = list[index + step]
        guard !row.isContainer, isPhoto(row.id), savedDescription(for: row.id) == nil else { return nil }
        return row.id
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
            guard let self, self.isInFront(),
                  self.infoTarget == nil, self.captionTarget == nil, !self.labelsSheetShown else { return false }
            // Redescribe is working on it: leave it alone, even if you arrow away and back, so the old
            // description isn't spoken first (as on Windows). Redescribe speaks the new one.
            guard (self.describingAgain[id] ?? 0) == 0 else { return false }
            return self.autoDescribeCandidate == id
        }
        describer.speak = { [weak self] text in self?.announce(text, queued: true) }
        describer.nextPhoto = { [weak self] id, step in self?.photoAhead(of: id, step: step) }
        describer.describeAhead = { [weak self] id in
            // Not downloaded from iCloud, and not saved if Redescribe took the photo meanwhile.
            _ = await self?.describeAndRemember(id, allowNetwork: false) { [weak self] in
                (self?.describingAgain[id] ?? 0) == 0
            }
        }
        describer.mayDescribeAhead = { [weak self] in
            // In front through the same check as isStillOn (which tests replace), so a sheet up also waits (#143's
            // review).
            let info = ProcessInfo.processInfo
            return (self?.isInFront() ?? false) && !info.isLowPowerModeEnabled
                && [.nominal, .fair].contains(info.thermalState)
        }
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
            self.keepSelectionShowing()
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
        return rows.filter { Self.matchesSearch($0, query) }
    }

    /// Whether a row is found by a search, already trimmed and not empty: its name, kind, caption, keywords or date,
    /// ignoring case, as on Windows.
    nonisolated static func matchesSearch(_ row: FileRow, _ query: String) -> Bool {
        row.name.localizedCaseInsensitiveContains(query)
            || row.kind.localizedCaseInsensitiveContains(query)
            || row.caption.localizedCaseInsensitiveContains(query)
            || row.keywords.localizedCaseInsensitiveContains(query)
            || row.dateText.localizedCaseInsensitiveContains(query)
    }

    var currentItem: SidebarItem? { selectedSourceID.flatMap { itemsByID[$0] } }
    var currentTitle: String { currentItem?.title ?? AppInfo.name }

    var statusText: String {
        if isLoading { return "Loading…" }
        if showingPhotosNoAccess { return "No access to Photos" }
        let visible = visibleRows
        let items = visible.count == 1 ? "1 item" : "\(visible.count.formatted()) items"
        // Only rows showing count, as only they're acted on (`selectedShowing`); a reload can also leave ids no longer listed.
        let selected = selection.isEmpty ? 0 : visible.reduce(0) { $0 + (selection.contains($1.id) ? 1 : 0) }
        return selected == 0 ? items : "\(selected.formatted()) of \(items) selected"
    }

    // MARK: - Startup

    /// Whether Photos access is given (full or limited). Without it the browser still works for added folders (#47).
    var photosAllowed: Bool { authState == .authorized }

    /// Whether the place showing is the "Photos library: no access" row.
    var showingPhotosNoAccess: Bool { currentItem?.kind == .photosNoAccess }

    /// Without Photos access, the first added folder is shown once its scan is in, rather than the no-access row.
    @ObservationIgnored private var showFirstFolderWhenScanned = false

    func start() async {
        guard authState == .unknown else { return }
        DiagnosticLog.started(appleIntelligence: describeUnavailableReason ?? "available")
        let status = await PHPhotoLibrary.requestAuthorization(for: .readWrite)
        try? FileManager.default.removeItem(at: PhotoExporter.temporaryRoot)
        guard status == .authorized || status == .limited else {
            // The browser still opens: added folders, describing and Get Info for files don't need Photos (#47).
            DiagnosticLog.write("start", "Photos access not given; \(folderRoots.count) added folders")
            authState = .denied
            rebuildSidebar()
            selectedSourceID = SidebarID.photosNoAccess
            showFirstFolderWhenScanned = !folderRoots.isEmpty
            refreshFolders()
            return
        }
        DiagnosticLog.write("start", "Photos access: \(status == .limited ? "limited" : "full"); \(folderRoots.count) added folders")
        await startPhotos(afterNoAccess: false)
    }

    /// Everything that reads the Photos library: its sections, the change observer, background describing of its
    /// photos. At launch, or when access is given while the app is running (`afterNoAccess`).
    private func startPhotos(afterNoAccess: Bool) async {
        authState = .authorized
        let observer = LibraryObserver { [weak self] change in
            Task { @MainActor in self?.libraryDidChange(change.value) }
        }
        self.observer = observer
        PHPhotoLibrary.shared().register(observer)

        if afterNoAccess {
            // On the no-access row: to All Photos, said. In a folder: stay there, and say the library is there now.
            if selectedSourceID == SidebarID.photosNoAccess {
                loadAnnouncementPrefix = "Showing "
            } else {
                announce("Photos library added to the sidebar", queued: true)
            }
            rebuildSidebar()
            if selectedSourceID == nil { selectedSourceID = SidebarID.allPhotos }
        } else {
            rebuildSidebar()
            selectedSourceID = SidebarID.allPhotos
            refreshFolders()
        }
        // Watched for new photos only while describing them in the background is on: it's a whole-library fetch.
        // The photos not described yet carry on from where the last run left off (#102). The added folders' photos
        // are already queued when access comes later.
        if describeInBackground {
            allPhotosFetch = PHAsset.fetchAssets(with: .image, options: nil)
            describeLibraryBacklog(announce: false, includeFolders: !afterNoAccess)
        }
        await refreshDateBuckets()
        rebuildSidebar()
        warmNameCache()
    }

    /// Back from another app (System Settings, say): if Photos access was given meanwhile, the library joins the
    /// sidebar without reopening the app.
    private func recheckPhotosAccess() {
        guard authState == .denied else { return }
        let status = PHPhotoLibrary.authorizationStatus(for: .readWrite)
        guard status == .authorized || status == .limited else { return }
        DiagnosticLog.write("start", "Photos access given while running: \(status == .limited ? "limited" : "full")")
        // Set now, not in the task: a second return to the app before it runs mustn't start the library twice.
        authState = .authorized
        Task { await startPhotos(afterNoAccess: true) }
    }

    /// Opens System Settings at Privacy & Security, Photos.
    func openPhotosPrivacySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Photos") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: - Sidebar

    static let hiddenSmartAlbums: Set<Int> = [
        PHAssetCollectionSubtype.smartAlbumUserLibrary.rawValue,
        PHAssetCollectionSubtype.smartAlbumAllHidden.rawValue,
        1_000_000_201, // Recently Deleted
    ]

    static let smartAlbumOrder: [PHAssetCollectionSubtype] = [
        .smartAlbumFavorites, .smartAlbumRecentlyAdded, .smartAlbumVideos, .smartAlbumLivePhotos,
        .smartAlbumScreenshots, .smartAlbumSelfPortraits, .smartAlbumDepthEffect, .smartAlbumPanoramas,
        .smartAlbumBursts, .smartAlbumSlomoVideos, .smartAlbumTimelapses, .smartAlbumCinematic,
        .smartAlbumLongExposures, .smartAlbumAnimated, .smartAlbumRAW,
    ]

    static func symbol(for subtype: PHAssetCollectionSubtype) -> String {
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

        // Without Photos access, one row stands in for Photos' sections; nothing is fetched from Photos (#47).
        guard photosAllowed else {
            let row = SidebarItem(id: SidebarID.photosNoAccess, title: SidebarID.photosNoAccessTitle, kind: .photosNoAccess,
                                  systemImage: "photo.badge.exclamationmark", count: 0, children: nil, parentID: nil,
                                  containerKind: "Library")
            photoSections = [SidebarSection(id: "library", title: "Photos", items: [row])]
            return applySidebar(previousItem: previousItem)
        }

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

        let dates = Self.dateItems(dateBuckets)

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
        photoSections = newSections
        return applySidebar(previousItem: previousItem)
    }

    /// Rebuilds only the Folders section, after a scan of the added folders (#47): Photos' sections are kept as they
    /// were, so their album counts aren't fetched again on the main actor.
    @discardableResult
    private func rebuildFolderSection() -> Bool {
        applySidebar(previousItem: currentItem)
    }

    /// Puts Photos' sections and the Folders section together and shows them, keeping the place showing in step.
    private func applySidebar(previousItem: SidebarItem?) -> Bool {
        var newSections = photoSections
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
            // A subfolder whose last photo was deleted in Finder drops out: go to the nearest folder above it that's
            // still listed and say so ("Showing Pictures, 12 items"), rather than to All Photos without a word (#47).
            // Without Photos access, to the first added folder still listed, or the no-access row.
            let place = FolderRoots.fallbackPlace(previousPath: previousItem?.directoryPath, roots: folderRoots,
                                                  photosAllowed: photosAllowed,
                                                  isListed: { index[FolderID.folder($0)] != nil })
            if previousItem?.isDirectory == true, place != SidebarID.allPhotos {
                loadAnnouncementPrefix = loadAnnouncementPrefix ?? "Showing "
            }
            selectedSourceID = place
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

    /// The Dates section: years newest first, each holding its months newest first, from photo counts by year and
    /// month.
    nonisolated static func dateItems(_ buckets: [Int: [Int: Int]]) -> [SidebarItem] {
        buckets.keys.sorted(by: >).map { year in
            let months = buckets[year] ?? [:]
            let yearID = "year:\(year)"
            let children = months.keys.sorted(by: >).map { month in
                SidebarItem(id: "month:\(year)-\(month)", title: monthTitle(year: year, month: month),
                            kind: .month(year: year, month: month), systemImage: "calendar",
                            count: months[month] ?? 0, children: nil, parentID: yearID, containerKind: "Month")
            }
            return SidebarItem(id: yearID, title: String(year), kind: .year(year), systemImage: "folder",
                               count: months.values.reduce(0, +), children: children, parentID: nil,
                               containerKind: "Year")
        }
    }

    nonisolated static func monthTitle(year: Int, month: Int) -> String {
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
        case .collectionList, .year, .directory, .photosNoAccess:
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

        // The no-access row lists nothing; the browser shows how to give access instead (#47).
        if item.kind == .photosNoAccess {
            currentFetch = nil
            assetsByID = [:]
            rows = []
            isLoading = false
            finishLoad(previousSelection: [], item: item, slow: false)
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
        if announceNextLoad || slow || loadAnnouncementPrefix != nil {
            announceNextLoad = false
            let prefix = loadAnnouncementPrefix ?? ""
            loadAnnouncementPrefix = nil
            // The no-access row has no items to count: just its name ("Showing Photos library: no access").
            announce(item.kind == .photosNoAccess ? "\(prefix)\(item.title)"
                     : "\(prefix)\(item.title), \(rows.count == 1 ? "1 item" : "\(rows.count.formatted()) items")")
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
        let scans = roots.map { root -> Task<FolderScan, Never> in
            let key = root.lowercased()
            folderScans[key]?.cancel()
            // The photos there at the last scan, so the new ones are worked out off the main actor too (#47).
            let before = folderMediaPaths[key]
            let justAdded = justAddedFolders.contains(key)
                || (describeInBackground && before == nil && !folderBacklogQueued.contains(key))
            let scan = Task.detached(priority: .utility) {
                FolderScan.scan(root, before: before, justAdded: justAdded)
            }
            folderScans[key] = scan
            return scan
        }
        Task {
            var fresh: [String: FolderNode] = [:]
            var results: [(Task<FolderScan, Never>, FolderScan)] = []
            for scan in scans { results.append((scan, await scan.value)) }
            // Checked once they're all in: a folder scanned again meanwhile has its older result left out.
            var newPhotos: [String] = [], backlog: [String] = []
            var addedNames: [String] = []
            for (scan, result) in results where !scan.isCancelled {
                let key = result.node.path.lowercased()
                fresh[key] = result.node
                if folderScans[key] == scan { folderScans[key] = nil }
                // A folder that's missing (an unplugged disk) keeps its last list of photos.
                guard let paths = result.paths, folderRoots.contains(where: { $0.lowercased() == key }) else { continue }
                // Its first scan since launch: every photo not described yet joins the backlog (#102). A folder just
                // added, and photos new since the last scan, go ahead of it.
                let firstScan = folderMediaPaths[key] == nil && !justAddedFolders.contains(key)
                folderMediaPaths[key] = paths
                if justAddedFolders.remove(key) != nil { addedNames.append(FolderRoots.displayName(result.node.path)) }
                if firstScan {
                    folderBacklogQueued.insert(key)
                    backlog += result.newPhotos
                } else {
                    newPhotos += result.newPhotos
                }
            }
            if describeInBackground {
                let text = addedNames.count == 1 ? "Describing {count} from \(addedNames[0]) in the background" : nil
                if !newPhotos.isEmpty { describeFilesLater(newPhotos, first: true, announce: text) }
                if !backlog.isEmpty { describeFilesLater(backlog, first: false, announce: nil) }
            }
            guard !fresh.isEmpty else { return }
            // In the order they were added; folders removed meanwhile drop out.
            let old = Dictionary(folderNodes.map { ($0.path.lowercased(), $0) }, uniquingKeysWith: { a, _ in a })
            folderNodes = folderRoots.compactMap { fresh[$0.lowercased()] ?? old[$0.lowercased()] }
            // Only the Folders section: Photos' album counts are left alone (#47).
            let reloaded = rebuildFolderSection()
            // Without Photos access, the first added folder rather than the no-access row, once it's listed (#47).
            if showFirstFolderWhenScanned {
                showFirstFolderWhenScanned = false
                if selectedSourceID == SidebarID.photosNoAccess,
                   let root = folderRoots.first(where: { itemsByID[FolderID.folder($0)] != nil }) {
                    selectedSourceID = FolderID.folder(root)
                    return
                }
            }
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

    /// View > Refresh (Shift-Command-R): reads the added folders and the place showing again, as Windows' Refresh (F5).
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
            if result.added {
                added.append(name)
                // Its photos are described in the background once it's scanned, when that's on (#47).
                justAddedFolders.insert(FolderRoots.normalize(path).lowercased())
            } else {
                already.append(name)
            }
        }
        folderRoots = roots
        FolderRoots.save(roots, to: defaults)
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
        FolderRoots.save(folderRoots, to: defaults)
        folderNodes.removeAll { FolderRoots.same($0.path, root) }
        folderMediaPaths[root.lowercased()] = nil
        justAddedFolders.remove(root.lowercased())
        // You're always in the folder being removed: go to All Photos first, so its place going away isn't announced
        // as if it happened by itself. Said as on Windows: "Removed Pictures from My Photo Library Described. Showing All Photos, 12 items".
        // Without Photos access, to the next added folder, or the no-access row (#47).
        loadAnnouncementPrefix = "Removed \(name) from \(AppInfo.name). Showing "
        selectedSourceID = FolderRoots.fallbackPlace(previousPath: nil, roots: folderRoots, photosAllowed: photosAllowed,
                                                     isListed: { self.itemsByID[FolderID.folder($0)] != nil })
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
                describeLater(details.insertedObjects, first: true)
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
        let ids = showing(ids)
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
        let files = Array(files(for: showing(ids)).prefix(200))
        if !files.isEmpty {
            previewURLs = files.map(\.url)
            previewURL = previewURLs.first
            return
        }
        let assets = Array(assets(for: showing(ids)).prefix(200))
        guard !assets.isEmpty else { NSSound.beep(); return }
        perform("Preparing preview…") {
            let urls = try await self.exportTemporary(assets)
            self.previewURLs = urls
            self.previewURL = urls.first
        }
    }

    func exportSelection(_ ids: Set<String>? = nil) {
        let assets = assets(for: showing(ids))
        let files = files(for: showing(ids))
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
        let files = files(for: showing(ids))
        if !files.isEmpty {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.writeObjects(files.map { $0.url as NSURL })
            announce(files.count == 1 ? "Copied \(files[0].name)" : "Copied \(files.count) items")
            return
        }
        let assets = assets(for: showing(ids))
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
        guard changesAllowed() else { return }
        let assets = assets(for: showing(ids))
        guard !assets.isEmpty else {
            // Windows makes a file a favorite by rating it five stars; writing file metadata on the Mac is to come.
            if !files(for: showing(ids)).isEmpty { announce("Favorites aren't available for files yet") } else { NSSound.beep() }
            return
        }
        let makeFavorite = !assets.allSatisfy(\.isFavorite)
        let box = UncheckedBox(value: assets)
        perform("Updating favorites…") {
            try await PhotoChanges.performLibraryChanges {
                for asset in box.value {
                    PHAssetChangeRequest(for: asset).isFavorite = makeFavorite
                }
            }
            self.announce(makeFavorite ? "Added to Favorites" : "Removed from Favorites")
        }
    }

    /// Photo > Rating (#20): sets the star rating of the chosen photos in Photos (macOS 27 lets apps write it), as
    /// Windows does for files. 0 removes it. Files in added folders aren't rated yet.
    func setRating(_ stars: Int, _ ids: Set<String>? = nil) {
        guard changesAllowed() else { return }
        guard canEditCaptions else {
            errorMessage = "Rating photos requires macOS 27 or later."
            return
        }
        let ids = showing(ids)
        let assets = assets(for: ids)
        guard !assets.isEmpty else {
            if !files(for: ids).isEmpty { announce("Ratings aren't available for files yet") } else { NSSound.beep() }
            return
        }
        let stars = min(max(stars, 0), 5)
        // Files chosen alongside library photos are left as they are, and that's said rather than skipped quietly.
        let skippedFiles = !files(for: ids).isEmpty
        let box = UncheckedBox(value: assets)
        perform("Saving rating…") {
            try await PhotoChanges.performLibraryChanges {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
                if #available(macOS 27, *) {
                    for asset in box.value {
                        PHAssetChangeRequest(for: asset).rating = PHAsset.Rating(rawValue: stars) ?? .unset
                    }
                }
#endif
            }
            self.announce(RatingText.done(stars) + (skippedFiles ? ". Files aren't rated yet" : ""))
        }
    }

    func showInfo(_ ids: Set<String>? = nil) {
        let ids = showing(ids)
        guard let row = rows.first(where: { ids.contains($0.id) && !$0.isContainer }) else {
            NSSound.beep()
            return
        }
        if let asset = assetsByID[row.id] {
            infoTarget = InfoTarget(id: row.id, source: .asset(asset), name: row.name)
        } else if let file = filesByID[row.id] {
            // The metadata the list read, if it has; opened before then, Get Info reads it itself (#47), so the row's
            // stand-ins (the file's creation date, no dimensions or caption) aren't shown as the file's.
            infoTarget = InfoTarget(id: row.id, source: .file(file, fileMetadata[file.descriptionKey]), name: row.name)
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
                // Read-only says so itself; that isn't something that went wrong.
                if !(error is PhotoChanges.ReadOnly) { DiagnosticLog.write("action", "\(label.trimmingCharacters(in: CharacterSet(charactersIn: "…"))) failed: \(Self.describe(error))") }
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
        guard changesAllowed() else { return }
        let ids = showing(ids)
        // Files in an added folder have their caption written into the file (#31).
        let files = files(for: ids)
        if !files.isEmpty {
            guard assets(for: ids).isEmpty else { announceMixedSelection(); return }
            editFileCaption(files)
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

    /// Edit Caption for files in an added folder (#31): the caption is the file's XMP description.
    private func editFileCaption(_ files: [MediaFile]) {
        let photos = files.filter { !$0.isVideo }
        guard !photos.isEmpty else { announce("Captions can't be saved in video files"); return }
        // Every one is a GIF or the like: say so now rather than after typing a caption. Some of them are skipped when
        // saving, and named then.
        let unsupported = FileWriteSummary.unsupported(photos)
        if unsupported.count == photos.count, let text = FileWriteSummary.skippedText(unsupported) {
            announce(text)
            return
        }
        let ids = photos.map { FolderID.file($0.path) }
        guard photos.count == 1 else {
            captionTarget = CaptionTarget(assetIDs: ids, initialText: "", inFiles: true)
            return
        }
        // The caption as the file has it now, not the list's copy: that's empty until the file's details are read,
        // and saving an empty field would remove the real caption.
        let url = photos[0].url
        Task {
            let caption = await Task.detached(priority: .userInitiated) { FileMetadata.read(url).caption }.value
            // Pressed twice: the sheet from the first press is already up, with whatever's been typed in it.
            guard captionTarget == nil else { return }
            captionTarget = CaptionTarget(assetIDs: ids, initialText: caption, inFiles: true)
        }
    }

    func saveCaption(_ text: String, for target: CaptionTarget) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let done = trimmed.isEmpty ? "Caption removed" : "Caption saved"
        let files = target.assetIDs.compactMap { filesByID[$0] }
        if !files.isEmpty {
            writeFilesAndSay(files.map { ($0, FileMetadataChanges(caption: trimmed)) }, label: "Saving caption…") { _ in done }
            return
        }
        let assets = target.assetIDs.compactMap { assetsByID[$0] }
        guard !assets.isEmpty else { return }
        writeCaptions(assets.map { ($0, trimmed) }, label: "Saving caption…", done: done)
    }

    /// Writes each photo's caption (an empty one removes it), then says `done` if given.
    private func writeCaptions(_ captions: [(PHAsset, String)], label: String, done: String?) {
        let box = UncheckedBox(value: captions)
        perform(label) {
            try await PhotoChanges.performLibraryChanges {
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

    /// Photo > Save Descriptions in Photos: off until the person agrees once (Kelly, 12 Oct).
    private(set) var saveInPhotos: Bool
    var captionPolicy: CaptionPolicy { saveInPhotos ? .whenEmpty : .never }

    /// Save Descriptions in Photos, as the Photo menu and Settings show it: paused, and unchecked, while read-only.
    var saveInPhotosTitle: String {
        readOnly && saveInPhotos ? "Save Descriptions in Photos (Paused in Read-Only Mode)" : "Save Descriptions in Photos"
    }

    var saveInPhotosShownOn: Bool { saveInPhotos && !readOnly }

    /// Chosen from the menu or Settings. While it's paused, choosing it turns it off, so it can always be turned off.
    func chooseSaveInPhotos(_ on: Bool) {
        setSaveInPhotos(readOnly && saveInPhotos ? false : on)
    }

    /// Turns Save Descriptions in Photos on, after asking, or off. Asking is the permission to change people's photos.
    func setSaveInPhotos(_ on: Bool) {
        if on, readOnly {
            announce(PhotoChanges.readOnlyMessage)
            return
        }
        if on {
            let alert = NSAlert()
            alert.messageText = "Save descriptions in your photos?"
            alert.informativeText = "From now on, each new description is also saved in the photo: as its caption when it has none, and in the file's accessibility fields for photos in added folders. Captions and alt text that you or other apps wrote are never replaced. You can turn this off at any time."
            alert.addButton(withTitle: "Save in Photos")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else {
                // Settings' checkbox already shows on: made again, so it shows off. Setting the same value again
                // wouldn't redraw it.
                switchesRedraw += 1
                return
            }
        }
        saveInPhotos = on
        defaults.set(on, forKey: CaptionPolicy.key)
        announce(on ? "New descriptions are saved in your photos" : "Descriptions aren't saved in your photos")
    }

    // MARK: - Place names (#105)

    /// Settings > Look Up Place Names: off until the person agrees once, since it sends coordinates to Apple.
    private(set) var lookUpPlaceNames: Bool

    /// Changed when a question behind a Settings checkbox is cancelled, so the checkbox is made again and shows off.
    private(set) var switchesRedraw = 0

    /// Turns place names on, after saying what's sent and to whom, or off.
    func setLookUpPlaceNames(_ on: Bool) {
        if on {
            let alert = NSAlert()
            alert.messageText = PlaceNames.question
            alert.informativeText = PlaceNames.explanation
            alert.addButton(withTitle: "Look Up Place Names")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else {
                switchesRedraw += 1
                return
            }
        }
        lookUpPlaceNames = on
        defaults.set(on, forKey: PlaceNames.key)
        announce(on ? "Get Info looks up place names" : "Place names aren't looked up")
    }

    /// Photo > Use Description as Caption: each selected photo's saved description becomes its caption. Asks before
    /// replacing captions people wrote.
    func useDescriptionAsCaption(_ ids: Set<String>? = nil) {
        guard changesAllowed() else { return }
        let ids = showing(ids)
        let files = files(for: ids)
        if !files.isEmpty {
            guard assets(for: ids).isEmpty else { announceMixedSelection(); return }
            useDescriptionAsCaption(files: files)
            return
        }
        guard canEditCaptions else {
            errorMessage = "Editing captions requires macOS 27 or later."
            return
        }
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
            let replacing = described.compactMap { row, asset, text -> (name: String, caption: String)? in
                let caption = (existing[asset.localIdentifier] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                return caption.isEmpty || caption == text ? nil : (row.name, caption)
            }
            guard confirmReplacing(replacing) else { return }
            var done = described.count == 1 ? "Caption saved" : "Captions saved for \(described.count) photos"
            if skipped > 0 { done += ". \(skipped == 1 ? "1 photo hasn't" : "\(skipped) photos haven't") been described yet" }
            writeCaptions(described.map { ($0.1, $0.2) }, label: "Saving caption…", done: done)
        }
    }

    /// Captions go to two different places for library photos (Photos) and files (the file itself), so a selection of
    /// both is done in two goes rather than half of it quietly left out.
    private func announceMixedSelection() {
        announce("Select either photos from the Photos library or files from a folder, not both, to change captions")
    }

    /// Asks before replacing what's already in files: captions, or alt text another app wrote. Each entry is a
    /// photo's name, the field ("caption" or "alt text") and what it holds now. True when there are none, or to go on.
    private func confirmReplacingInFiles(_ replacing: [(name: String, field: String, current: String)]) -> Bool {
        guard !replacing.isEmpty else { return true }
        let alert = NSAlert()
        if Set(replacing.map(\.name)).count == 1 {
            let fields = replacing.map(\.field)
            alert.messageText = "Replace the \(fields.joined(separator: " and ")) of \(replacing[0].name)?"
            alert.informativeText = replacing.map { "Its \($0.field) is: \($0.current)" }.joined(separator: "\n")
        } else {
            alert.messageText = "Replace what's already in \(Set(replacing.map(\.name)).count) photos?"
            alert.informativeText = "Their captions or alt text will be replaced with their descriptions."
        }
        alert.addButton(withTitle: "Replace")
        alert.addButton(withTitle: "Cancel")
        return alert.runModal() == .alertFirstButtonReturn
    }

    /// Asks before replacing captions people wrote, by photo name and caption. True when there are none, or to go on.
    private func confirmReplacing(_ replacing: [(name: String, caption: String)]) -> Bool {
        guard !replacing.isEmpty else { return true }
        let alert = NSAlert()
        alert.messageText = replacing.count == 1 ? "Replace the caption of \(replacing[0].name)?"
            : "Replace the captions of \(replacing.count) photos?"
        alert.informativeText = replacing.count == 1 ? "Its caption is: \(replacing[0].caption)"
            : "Their captions will be replaced with their descriptions."
        alert.addButton(withTitle: "Replace")
        alert.addButton(withTitle: "Cancel")
        return alert.runModal() == .alertFirstButtonReturn
    }

    /// Use Description as Caption for files in an added folder (#31). Each description goes into the file's caption
    /// and its accessibility fields (the alt text, and the extended description when the alt text is shorter), with
    /// the same question before replacing a caption as for Photos.
    private func useDescriptionAsCaption(files: [MediaFile]) {
        let photos = files.filter { !$0.isVideo }
        guard !photos.isEmpty else { announce("Captions can't be saved in video files"); return }
        let described = photos.compactMap { file -> (MediaFile, String)? in
            guard let text = descriptions.description(forKey: file.descriptionKey)?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !text.isEmpty else { return nil }
            return (file, text)
        }
        guard !described.isEmpty else {
            announce(photos.count == 1 ? "\(photos[0].name) hasn't been described yet. Describe it first."
                     : "None of these photos have been described yet.")
            return
        }
        let notDescribed = photos.count - described.count
        Task {
            // The captions as the files have them now, not the list's copy, which is empty until their details are
            // read. Files that can't hold a caption are skipped, so they aren't asked about.
            let urls = described.map(\.0.url)
            let existing = await Task.detached(priority: .userInitiated) { urls.map { FileMetadata.read($0) } }.value
            let replacing = zip(described, existing).flatMap { pair, metadata -> [(name: String, field: String, current: String)] in
                let (file, text) = pair
                guard FileMetadataWriter.canWrite(name: file.name) else { return [] }
                return FileFields.replaced(caption: metadata.caption, altText: metadata.altText,
                                           extendedDescription: metadata.extendedDescription, with: text)
                    .map { (file.name, $0.field, $0.current) }
            }
            guard confirmReplacingInFiles(replacing) else { return }
            writeFilesAndSay(described.map { ($0.0, FileMetadataChanges(caption: $0.1, accessibilityDescription: $0.1)) },
                             label: "Saving caption…", notDescribed: notDescribed) { count in
                count == 1 ? "Caption saved" : "Captions saved for \(count) photos"
            }
        }
    }

    // MARK: - Writing into files (#31)

    /// Writes into files in an added folder with the status bar showing `label`, then says what happened: `done` with
    /// how many were written, the files skipped because their format can't hold the fields, and the photos not yet
    /// described. Files that couldn't be changed are shown in an error.
    private func writeFilesAndSay(_ items: [(MediaFile, FileMetadataChanges)], label: String, notDescribed: Int = 0,
                                  done: @escaping (Int) -> String) {
        Task {
            activity = label
            let outcome = await writeFiles(items)
            activity = nil
            let text = FileWriteSummary.text(done: outcome.written.isEmpty ? nil : done(outcome.written.count),
                                             skipped: outcome.skipped, secondPicture: outcome.secondPicture,
                                             notDescribed: notDescribed)
            if !text.isEmpty { announce(text) }
            if let failure = outcome.failed.first {
                errorMessage = outcome.failed.count == 1 ? "\(failure.name) couldn't be saved. \(failure.reason)"
                    : "\(outcome.failed.count) files couldn't be saved. \(failure.name): \(failure.reason)"
            }
        }
    }

    /// What writing into files came to.
    private struct FileWriteOutcome {
        var written: [MediaFile] = []
        var skipped: [(name: String, format: String)] = []
        /// Photos holding a second picture (an HDR map) whose index couldn't be kept right (#90).
        var secondPicture: [String] = []
        var failed: [(name: String, reason: String)] = []
    }

    /// Writes into files off the main actor, after any write already under way, then shows what each file says now
    /// and keeps its description: writing changes a file's size, which is part of its description's key. Failures
    /// are logged.
    private func writeFiles(_ items: [(MediaFile, FileMetadataChanges)]) async -> FileWriteOutcome {
        let previous = fileWrites
        let cache = descriptions
        typealias Written = (before: MediaFile, now: MediaFile, metadata: FileMetadata)
        let work = Task.detached(priority: .userInitiated) { () -> [(MediaFile, Result<Written, Error>)] in
            await previous?.value
            return items.map { file, changes in
                (file, Result {
                    // As the file is now, not when it was listed: a write just before this one changed its size, and
                    // moved its description to match.
                    let before = FolderScanner.file(at: file.path) ?? file
                    let metadata = try FileMetadataWriter.write(changes, to: file.url)
                    let now = FolderScanner.file(at: file.path) ?? before
                    // Moved here, straight after the write and before the next one reads the file's size, so a
                    // description can never be left under a key that no longer matches.
                    cache.rekey(from: before.descriptionKey, to: now.descriptionKey)
                    return (before, now, metadata)
                })
            }
        }
        fileWrites = Task { _ = await work.value }
        var outcome = FileWriteOutcome()
        for (file, result) in await work.value {
            switch result {
            case .success(let written):
                fileWritten(written.before, now: written.now, metadata: written.metadata)
                outcome.written.append(written.now)
            case .failure(FileMetadataWriteError.unsupportedFormat(let format)):
                outcome.skipped.append((file.name, format))
            case .failure(FileMetadataWriteError.multiPicture):
                outcome.secondPicture.append(file.name)
            case .failure(let error):
                Self.log.error("Couldn't write into \(file.name, privacy: .public): \(error.localizedDescription, privacy: .public)")
                DiagnosticLog.write("save", "Couldn't write into \(file.name): \(Self.describe(error))")
                outcome.failed.append((file.name, error.localizedDescription))
            }
        }
        return outcome
    }

    /// A file My Photo Library Described just wrote into: its description moves to the file's new key, and its row shows the new
    /// caption and alt text. The folder watcher lists the folder again shortly after, and finds all this in place.
    private func fileWritten(_ old: MediaFile, now: MediaFile, metadata: FileMetadata) {
        fileMetadata[now.descriptionKey] = metadata
        let id = FolderID.file(now.path)
        // Gone to another place meanwhile: the file is read afresh when its folder is shown again.
        guard filesByID[id] != nil else { return }
        filesByID[id] = now
        if let index = rows.firstIndex(where: { $0.id == id }) {
            // From the cache under the file's new key: the column may have been refilled while the row still had the old.
            let description = showsDescriptionColumn ? descriptions.description(forKey: now.descriptionKey) ?? "" : ""
            rows[index] = FileRow(file: now, metadata: metadata).withDescription(description)
        }
    }

    /// Whether a row is a file in an added folder, which can hold accessibility fields (#31).
    func isFile(_ id: String) -> Bool { filesByID[id] != nil }

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

    /// A description was just saved: with Save Descriptions in Photos on, it fills an empty caption too, quietly.
    private func captionFromNewDescription(_ text: String, key: String) {
        // Read-only: descriptions made now aren't saved into photos, then or later (as on Windows).
        guard !readOnly else { return }
        let policy = captionPolicy
        guard policy != .never else { return }
        if key.hasPrefix("file:") {
            captionFileFromNewDescription(text, key: key, policy: policy)
            return
        }
        guard canEditCaptions, photosAllowed else { return }
        let names = self.names
        Task.detached(priority: .utility) {
            // The caption as Photos has it now: never the list's copy, which is empty until its details load, so
            // an unloaded caption is never mistaken for none.
            guard let asset = PHAsset.fetchAssets(withLocalIdentifiers: [key], options: RowBuilder.fetchOptions()).firstObject
            else { return }
            let existing = AssetDetails.load(for: asset, names: names).caption
            guard policy.shouldWrite(text, over: existing) else { return }
            await Self.writeCaptionQuietly(UncheckedBox(value: asset), text.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    /// Save Descriptions in Photos for a file in an added folder (#31): the description goes into the file's caption
    /// and accessibility fields, as Use Description as Caption does, quietly. Only for a file still listed: its key
    /// holds its path in capitals, which can't be turned back into the path itself.
    private func captionFileFromNewDescription(_ text: String, key: String, policy: CaptionPolicy) {
        guard let file = filesByID.values.first(where: { $0.descriptionKey == key }), !file.isVideo,
              FileMetadataWriter.canWrite(name: file.name) else { return }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let url = file.url
        Task {
            // The caption and alt text as the file has them now, never the list's copy, which is empty until its
            // details are read.
            let existing = await Task.detached(priority: .utility) { FileMetadata.read(url) }.value
            var changes = FileMetadataChanges()
            if policy.shouldWrite(trimmed, over: existing.caption) { changes.caption = trimmed }
            // Only empty alt text is filled; what another app wrote is left alone, as a caption is.
            if FileFields.mayWriteAccessibility(trimmed, altText: existing.altText, extendedDescription: existing.extendedDescription,
                                                policy: policy) {
                changes.accessibilityDescription = trimmed
            }
            guard changes != FileMetadataChanges() else { return }
            // Without the status bar or an error dialog, as for Photos; failures are logged.
            _ = await writeFiles([(file, changes)])
        }
    }

    /// Writes a caption without the status bar or an error dialog: automatic captions can come one after another
    /// while describing in the background, and one dialog each would bury the person. Failures are logged.
    nonisolated private static func writeCaptionQuietly(_ asset: UncheckedBox<PHAsset>, _ caption: String) async {
        do {
            try await PhotoChanges.performLibraryChanges {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
                if #available(macOS 27, *) {
                    PHAssetChangeRequest(for: asset.value).caption = caption
                }
#endif
            }
        } catch {
            log.error("Couldn't save a description as a caption: \(error.localizedDescription, privacy: .public)")
            if !(error is PhotoChanges.ReadOnly) {
                DiagnosticLog.write("save", "Couldn't save a description as a caption: \(Self.describe(error))")
            }
        }
    }

    // MARK: - Describing in the background

    /// Photo > Describe Photos in the Background (#102): every photo not described yet, newest first, now and at each
    /// launch until they're done.
    func setDescribeInBackground(_ on: Bool) {
        describeInBackground = on
        backgroundProblem = nil
        // Photos' own photos only with access to them; added folders either way (#47).
        allPhotosFetch = on && photosAllowed ? PHAsset.fetchAssets(with: .image, options: nil) : nil
        guard on else {
            let left = stopBackgroundDescribing(announce: false)
            announce(left > 0 ? "Background describing off. Stopped with \(left == 1 ? "1 photo" : "\(left) photos") not described."
                     : "Photos won't be described in the background")
            return
        }
        if let reason = backgroundUnavailableReason {
            announce("Photos can't be described in the background. \(reason)")
            return
        }
        // Said once; the run's finish is said too, since the person started it.
        announce("Photos in the library will be described in the background")
        describeLibraryBacklog(announce: true)
    }

    /// Queues every photo not described yet: the Photos library's newest first (with access to it), then the added
    /// folders' photos (unless `includeFolders` is false: they're queued already).
    private func describeLibraryBacklog(announce: Bool, includeFolders: Bool = true) {
        guard backgroundUnavailableReason == nil else { return }
        let folderPaths = includeFolders
            ? folderMediaPaths.keys.sorted().flatMap { folderMediaPaths[$0].map { Array($0).sorted() } ?? [] } : []
        if includeFolders { folderBacklogQueued.formUnion(folderMediaPaths.keys) }
        let options = PHFetchOptions()
        options.sortDescriptors = [NSSortDescriptor(key: "creationDate", ascending: false)]
        // Nothing is asked of Photos without access to it (#47).
        let fetch = photosAllowed ? PHAsset.fetchAssets(with: .image, options: options) : nil
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        guard #available(macOS 27, *) else { return }
        let describer = makeBackgroundDescriber()
        // Stop Background Describing during the batches drops the rest, rather than starting again.
        let generation = describer.generation
        Task {
            // In batches, so a big library doesn't hold up the window.
            var index = 0
            while let fetch, index < fetch.count, describeInBackground, describer.generation == generation {
                let end = min(index + 500, fetch.count)
                describer.enqueue(fetch.objects(at: IndexSet(integersIn: index..<end)), sayFinish: announce,
                                  since: generation)
                index = end
                await Task.yield()
            }
            if !folderPaths.isEmpty {
                await describer.enqueue(filePaths: folderPaths, since: generation)
            }
        }
#endif
    }

    /// Why background describing can't work on this Mac, or nil when it can.
    var backgroundUnavailableReason: String? {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) { return ImageConversation.unavailableReason }
#endif
        return "Describing photos needs Apple Intelligence on macOS 27 or later."
    }

    var isDescribingInBackground: Bool { backgroundProgress != nil }

    /// Photo > Stop Background Describing: stops this run; the setting stays on, so it carries on at the next launch.
    @discardableResult
    func stopBackgroundDescribing(announce shouldAnnounce: Bool = true) -> Int {
        var left = 0
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *), let describer = backgroundDescriber as? BackgroundDescriber {
            left = describer.stop()
        }
#endif
        if shouldAnnounce {
            announce(left > 0 ? "Stopped describing in the background. \(left == 1 ? "1 photo" : "\(left) photos") not described; it carries on the next time \(AppInfo.name) starts."
                     : "Nothing is being described in the background")
        }
        return left
    }

    private func describeLater(_ assets: [PHAsset], first: Bool = false, announce text: String? = nil) {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) { makeBackgroundDescriber().enqueue(assets, first: first, announce: text) }
#endif
    }

    /// Photos added to an added folder (or in a folder just added), by path (#47). Only their descriptions are saved;
    /// nothing goes into the files unless Save Descriptions in Photos is on, through the usual path.
    private func describeFilesLater(_ paths: [String], first: Bool, announce text: String?) {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            let describer = makeBackgroundDescriber()
            Task { await describer.enqueue(filePaths: paths, first: first, announce: text) }
        }
#endif
    }

#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
    @available(macOS 27, *)
    private func makeBackgroundDescriber() -> BackgroundDescriber {
        if let describer = backgroundDescriber as? BackgroundDescriber { return describer }
        let describer = BackgroundDescriber(cache: descriptions) { [weak self] text in
            self?.backgroundProgress = text
            if text != nil { self?.backgroundProblem = nil }
        }
        describer.stopped = { [weak self] message in self?.backgroundProblem = message }
        describer.isOn = { [weak self] in self?.describeInBackground ?? false }
        // A photo whose folder was taken out of the library meanwhile isn't described.
        describer.isForegroundBusy = { [weak self] in
            guard let self else { return false }
            return self.autoDescriber.describingID != nil || self.autoDescriber.aheadID != nil
                || self.describingIDs.keys.contains { self.focusedRow?.id == $0 }
        }
        describer.isWanted = { [weak self] path in
            guard let self else { return false }
            return FolderRoots.root(containing: path, in: self.folderRoots) != nil
        }
        backgroundDescriber = describer
        return describer
    }
#endif

    // MARK: - Describe Photo

    /// Photo > Describe Photo (Command-D): describes the photo you're on with Apple Intelligence and speaks it,
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
        // Redescribe is replacing this one: the saved description is the old one, so wait for the new (#58).
        if (describingAgain[row.id] ?? 0) > 0 {
            announce("Redescribing \(row.name)")
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
        if autoDescriber.describingID == row.id || autoDescriber.aheadID == row.id {
            announce("Describing \(row.name)")
            return
        }
        autoDescriber.describingManually(row.id)
        describeTask?.cancel()
        announce("Describing \(row.name)")
        let id = row.id
        describeTaskID = id
        describeTask = Task { [weak self] in
            guard let text = await self?.describeAndRemember(id, allowNetwork: true) else { return }
            self?.say(text, ifStillOn: id)
        }
    }

    /// Photo > Redescribe (Command-R): describes the photo you're on afresh, replaces its saved
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
            alert.addButton(withTitle: "Redescribe")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        let id = row.id
        autoDescriber.describingManually(id)
        describingAgain[id, default: 0] += 1
        // Its own task: Describe Photo on another photo doesn't cut it short (#58); only a newer Redescribe does.
        describeAgainTask?.cancel()
        // A Describe Photo still running on this photo would save and speak an older description after this one.
        if describeTaskID == id { describeTask?.cancel() }
        announce("Redescribing \(row.name)")
        describeAgainTask = Task { [weak self] in
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

    /// A newer Mac release, found by the quiet check at launch or by Check for Updates.
    /// Remembered across restarts until it's installed (#104), so Help keeps saying "Update to X available…".
    private(set) var availableUpdate: Updates.Release? {
        didSet { Updates.remember(availableUpdate, in: defaults) }
    }

    /// Once a day at most, a moment after launch: is there a newer release? Says nothing; the Help menu item changes.
    func checkForUpdatesQuietly() {
        let defaults = self.defaults
        if let last = defaults.object(forKey: "lastUpdateCheck") as? Date, Date().timeIntervalSince(last) < 86_400 { return }
        Task {
            try? await Task.sleep(for: .seconds(5))
            // Offline: keep what's remembered. Otherwise this is the newer release, or nil when up to date.
            guard case .some(let release) = await Updates.latest(newerThan: AppInfo.shortVersion) else { return }
            defaults.set(Date(), forKey: "lastUpdateCheck")
            availableUpdate = release
        }
    }

    /// The first launch after an update: says so, with See What's New, as the apps' shared update experience asks
    /// (The-Idea-Place-Projects#12). Not on a first run or after going back to an older version.
    func sayIfJustUpdated() {
        let defaults = self.defaults
        let current = AppInfo.shortVersion
        let last = defaults.string(forKey: "lastRunVersion")
        defaults.set(current, forKey: "lastRunVersion")
        guard Updates.justUpdated(last: last, current: current) else { return }
        let alert = NSAlert()
        alert.messageText = "\(AppInfo.name) Update Installed"
        alert.informativeText = "\(AppInfo.name) is now version \(current)."
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "See what's new")
        // Escape closes it too, as the shared pattern asks; an alert only does that by itself for a Cancel button.
        let escape = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            guard event.keyCode == 53 else { return event }
            NSApp.stopModal(withCode: .alertFirstButtonReturn)
            return nil
        }
        defer { if let escape { NSEvent.removeMonitor(escape) } }
        if alert.runModal() == .alertSecondButtonReturn { NSWorkspace.shared.open(AppInfo.releaseNotesURL(current)) }
    }

    /// Help > Check for Updates…: says whether there's a newer version, and opens its download page if you want it.
    /// Always asks GitHub again, so a remembered release that was since withdrawn is forgotten; offline, the
    /// remembered one is still offered.
    func checkForUpdates() {
        announce("Checking for updates")
        Task {
            switch await Updates.latest(newerThan: AppInfo.shortVersion) {
            case .none:
                if let release = availableUpdate {
                    offer(release)
                } else {
                    errorMessage = "Couldn't check for updates. Check your internet connection and try again."
                }
            case .some(.none):
                defaults.set(Date(), forKey: "lastUpdateCheck")
                availableUpdate = nil
                let alert = NSAlert()
                alert.messageText = "\(AppInfo.name) is up to date"
                alert.informativeText = "Version \(AppInfo.shortVersion) is the newest."
                alert.runModal()
            case .some(.some(let release)):
                availableUpdate = release
                offer(release)
            }
        }
    }

    private func offer(_ release: Updates.Release) {
        let alert = NSAlert()
        alert.messageText = "Version \(release.version) of \(AppInfo.name) is available"
        alert.informativeText = "You have version \(AppInfo.shortVersion). The download page opens in your browser: open the disk image and drag \(AppInfo.name) to Applications, replacing the old one. Your descriptions and settings are kept."
        alert.addButton(withTitle: "Download")
        alert.addButton(withTitle: "Not Now")
        if alert.runModal() == .alertFirstButtonReturn { NSWorkspace.shared.open(release.page) }
    }

    /// Help > Check System chosen; an open Check System window checks again.
    var systemCheckRequest = 0

    /// Help > Check System.
    /// How many of the library's photos are described, for Check System; nil until counted.
    private(set) var libraryCoverage: (described: Int, total: Int)?
    @ObservationIgnored private var coverageCount: Task<Void, Never>?

    /// Counts the library's photos and how many are described, off the main actor: the Photos library's photos and
    /// the added folders' (videos aren't described). Each file's description is keyed by its size and date, so
    /// each is looked at.
    func countLibraryCoverage() {
        coverageCount?.cancel()
        // Counting again: "Counting…" until it's done, rather than the last count.
        libraryCoverage = nil
        let cache = descriptions
        let files = folderMediaPaths.values.flatMap { $0 }.filter { !MediaFiles.isVideo(($0 as NSString).lastPathComponent) }
        let photosAllowed = authState == .authorized
        coverageCount = Task {
            let work = Task.detached(priority: .utility) { () -> (Int, Int)? in
                var described = 0, total = 0
                if photosAllowed {
                    let photos = PHAsset.fetchAssets(with: .image, options: nil)
                    total += photos.count
                    photos.enumerateObjects { asset, _, stop in
                        if Task.isCancelled { stop.pointee = true }
                        if cache.description(forKey: asset.localIdentifier) != nil { described += 1 }
                    }
                }
                for path in Set(files) {
                    if Task.isCancelled { return nil }
                    guard let file = FolderScanner.file(at: path) else { continue }
                    total += 1
                    if cache.description(forKey: file.descriptionKey) != nil { described += 1 }
                }
                return Task.isCancelled ? nil : (described, total)
            }
            // A detached task doesn't hear its caller's cancellation, so it's passed on.
            let result = await withTaskCancellationHandler { await work.value } onCancel: { work.cancel() }
            guard !Task.isCancelled, let result else { return }
            libraryCoverage = (result.0, result.1)
        }
    }

    func systemCheck() -> SystemCheck {
#if arch(arm64)
        let appleSilicon = true
#else
        let appleSilicon = false
#endif
        var aiModelState: String?
        var aiLanguage: String?
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            aiModelState = ImageConversation.modelState
            aiLanguage = ImageConversation.languageText
        }
#endif
        let stats = DescribeStats.session
        return SystemCheck(.init(
            appName: AppInfo.name, appVersion: AppInfo.version,
            osVersion: ProcessInfo.processInfo.operatingSystemVersion, appleSilicon: appleSilicon,
            aiUnavailableReason: describeUnavailableReason,
            photosAccess: PHPhotoLibrary.authorizationStatus(for: .readWrite),
            savedDescriptions: descriptions.count, libraryCoverage: libraryCoverage,
            describeAutomatically: describeAutomatically, describeInBackground: describeInBackground,
            osBuild: SystemDetails.osBuild, chip: SystemDetails.chip, memory: SystemDetails.memory(),
            power: SystemDetails.power(), aiModelState: aiModelState, aiLanguage: aiLanguage,
            describeTimings: stats.timings(), recentFailures: stats.recentFailures(),
            aiDescriptionCheck: lastAIDescriptionCheck?.reportSummary))
    }

    /// Help > Check AI Description chosen; an open Check AI Description window describes again.
    var aiDescriptionCheckRequest = 0

    /// Check AI Description's last result this session, for Check System and Report a Bug.
    private(set) var lastAIDescriptionCheck: AIDescriptionCheck?

    /// Help > Check AI Description (#95): describes the sample photo that comes with the app, through the same path as
    /// Describe Photo, with the prompt in use. Saves nothing and touches no photo. Nil when it was given up (the window
    /// closed).
    func runAIDescriptionCheck() async -> AIDescriptionCheck? {
        let system = SystemCheck.osText(ProcessInfo.processInfo.operatingSystemVersion, build: SystemDetails.osBuild)
        let standardPrompt = DescriptionPrompt.current == DescriptionPrompt.standard
        func result(_ description: String?, took: Double, code: String? = nil, message: String? = nil) -> AIDescriptionCheck {
            let check = AIDescriptionCheck(description: description, took: took, code: code, message: message,
                                           standardPrompt: standardPrompt, modelSystem: system)
            lastAIDescriptionCheck = check
            DiagnosticLog.write("describe", "Check AI Description: \(check.reportSummary)")
            return check
        }
        // Apple Intelligence can't describe here: say so rather than try.
        if let reason = describeUnavailableReason { return result(nil, took: 0, code: "unavailable", message: reason) }
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            guard let url = Bundle.main.url(forResource: "SamplePhoto", withExtension: "jpg"),
                  let data = try? Data(contentsOf: url) else {
                return result(nil, took: 0, code: "missing_sample", message: "The sample photo is missing from the app")
            }
            let start = ContinuousClock.now
            do {
                let text = try await PhotoDescriber.describe(data)
                guard !Task.isCancelled else { return nil }
                guard let text else {
                    return result(nil, took: PhotoDescriber.seconds(since: start), code: "decode_failed",
                                  message: "The sample photo couldn't be read")
                }
                return result(text, took: PhotoDescriber.seconds(since: start))
            } catch {
                guard !(error is CancellationError), !Task.isCancelled else { return nil }
                return result(nil, took: PhotoDescriber.seconds(since: start), code: ImageConversation.failureCode(for: error),
                              message: ImageConversation.message(for: error))
            }
        }
#endif
        return result(nil, took: 0, code: "unavailable", message: "Describing photos needs Apple Intelligence on macOS 27 or later")
    }

    /// An error for the diagnostic log: its type and code as well as its message, never a photo's content.
    nonisolated static func describe(_ error: Error) -> String {
        let ns = error as NSError
        return "\(type(of: error)) \(ns.domain) \(ns.code): \(error.localizedDescription)"
    }

    /// Help > Open Log Folder (#103): the diagnostic log's folder in Finder.
    func openLogFolder() {
        try? FileManager.default.createDirectory(at: DiagnosticLog.folder, withIntermediateDirectories: true)
        if !NSWorkspace.shared.open(DiagnosticLog.folder) {
            errorMessage = "The log folder couldn't be opened. It's in your Library folder, under Application Support/\(AppInfo.dataFolderName)/Logs."
        }
    }

    /// The report Report a Bug's form starts from (#33): the system check, and Check AI Description's result or the
    /// last crash when it came from there. Set just before the form opens.
    private(set) var bugReportDraft = BugReport()
    /// Bumped with each new draft, so a form that's already open takes the new details.
    private(set) var bugReportRequest = 0
    /// Bumped to have the main window open the form, for the crash offer, which isn't in a view.
    private(set) var bugReportWindowRequest = 0

    /// Help > Report a Bug…, and the Report a Bug… buttons in Check System and Check AI Description: a fresh draft
    /// for the form. From Check AI Description, its whole result goes with it, as before.
    func prepareBugReport(aiCheck: AIDescriptionCheck? = nil, attachments: [BugReport.Attachment] = [], title: String = "") {
        var report = BugReport()
        report.title = title
        report.systemText = systemCheck().reportText
        if let aiCheck { report.attachments.append(.init(name: "the AI description check", heading: "Check AI Description", text: aiCheck.reportText)) }
        report.attachments += attachments
        bugReportDraft = report
        bugReportRequest += 1
    }

    /// Sends the report through the relay. When that can't be done (no key in this copy, or the relay refused or
    /// couldn't be reached), opens the prefilled GitHub page instead, with the whole report on the clipboard in case
    /// the page doesn't take it all.
    func sendBugReport(_ report: BugReport) async -> BugReportRelay.Result {
        let result = await BugReportRelay.send(report, key: AppInfo.relayKey)
        if BugReportRelay.needsBrowser(result) {
            // On the clipboard now; the page opens when the person chooses Open in Browser.
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(report.clipboardText, forType: .string)
        } else if case .filed(let number, _) = result {
            DiagnosticLog.write("report", "Filed as issue #\(number)")
        }
        return result
    }

    /// The prefilled GitHub issue page, for a report the relay didn't file.
    func openBugReportPage(_ report: BugReport) {
        NSWorkspace.shared.open(report.issuePageURL())
    }

    /// At launch, when the last run didn't quit cleanly (#33): asks once whether to report it. Report… opens the form
    /// with the start of macOS's crash report and the end of today's log, for the person to read before sending.
    func offerCrashReport() {
        guard let since = CrashCheck.uncleanLaunch else { return }
        CrashCheck.uncleanLaunch = nil
        DiagnosticLog.write("start", "The last run didn't quit cleanly")
        let alert = NSAlert()
        alert.messageText = "\(AppInfo.name) quit unexpectedly last time. Report it?"
        alert.informativeText = "The report opens in a form, where you can read and change it before it's sent. Reports are public."
        alert.addButton(withTitle: "Report…")
        // Escape answers Not Now, as it would Cancel.
        alert.addButton(withTitle: "Not Now").keyEquivalent = "\u{1b}"
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        prepareBugReport(attachments: CrashCheck.attachments(since: since), title: "Quit unexpectedly")
        bugReportWindowRequest += 1
    }

    /// Why photos can't be described on this Mac right now, or nil when they can. Never sets anything up.
    var describeUnavailableReason: String? {
        guard let photoDescribing else { return "Describing photos needs Apple Intelligence on macOS 27 or later." }
        return photoDescribing.unavailableReason()
    }

    /// Describes the photo with Apple Intelligence and saves the description. Returns what to say: the description,
    /// or why there isn't one; nil for a row that isn't a photo. Nil too, saying nothing, when the photo is only in
    /// iCloud and `allowNetwork` is false, or when a newer request replaced this one. A file in an added folder is
    /// read from disk and saved under its file key (#36).
    /// `replacingEdit` is the correction the person agreed Redescribe may replace; any other correction (one made
    /// meanwhile in the Description panel) is kept.
    private func describeAndRemember(_ id: String, allowNetwork: Bool, replacingEdit: String? = nil,
                                     keep: @MainActor () -> Bool = { true }) async -> String? {
        describingIDs[id, default: 0] += 1
        defer {
            describingIDs[id, default: 1] -= 1
            if describingIDs[id] == 0 { describingIDs[id] = nil }
        }
        guard let photoDescribing else {
            guard descriptionKey(for: id) != nil else { return nil }
            return "Describing photos needs Apple Intelligence on macOS 27 or later."
        }
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
        // Dropped while loading (describing ahead, and you landed elsewhere): stop before the model is asked.
        guard !Task.isCancelled else { return nil }
        guard let data else { return "This photo couldn't be loaded" }
        do {
            guard let text = try await photoDescribing.describe(data), !text.isEmpty else {
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
            return photoDescribing.failureMessage(error)
        }
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

    /// Home and End in the gallery: the first or last item, whatever's selected now.
    func moveSelectionToEnd(last: Bool) {
        let rows = visibleRows
        guard !rows.isEmpty else { NSSound.beep(); return }
        select(at: last ? rows.count - 1 : 0, in: rows)
    }

    /// Go > Next Photo (Command-Right Arrow) and Previous Photo (Command-Left), in the list or gallery: the next photo
    /// or video, past folders, without moving focus. So you can stay in the Description panel (reading with a braille
    /// display, say) and step from photo to photo; its description follows (Kelly, 13 Oct).
    func stepPhoto(by direction: Int) {
        let rows = visibleRows
        guard !rows.isEmpty else { NSSound.beep(); return }
        let current = rows.firstIndex { selection.contains($0.id) }
        var index = current.map { $0 + direction } ?? (direction > 0 ? 0 : rows.count - 1)
        // Photos only: a folder or video would take the Description panel's text box away, and focus with it.
        while rows.indices.contains(index), rows[index].isContainer || !isPhoto(rows[index].id) { index += direction }
        guard rows.indices.contains(index) else {
            announce(direction > 0 ? "No next photo" : "No previous photo")
            return
        }
        select(at: index, in: rows)
        scrollRequest = PaneScroll(id: rows[index].id, serial: scrollRequest.serial + 1)
    }

    /// Asks the list to scroll to a row chosen without it (Next Photo, Previous Photo).
    private(set) var scrollRequest = PaneScroll()

    /// Whether Next Photo and Previous Photo apply: not while Get Info, a caption or Labels is in front.
    var canStepPhotos: Bool { infoTarget == nil && captionTarget == nil && !labelsSheetShown }

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
        // The gallery says what View > Labels > Gallery chooses (#38); the list keeps its name and position.
        announce(viewMode == .gallery ? galleryText(for: rows[index], at: index, of: rows.count)
                                      : "\(rows[index].name), \(index + 1) of \(rows.count)")
    }

    /// The photo as the gallery reads it, from the items chosen in View > Labels > Gallery.
    func galleryText(for row: FileRow, at index: Int, of count: Int) -> String {
        GalleryLabels.text(for: row, position: (index + 1, count), items: galleryLabels)
    }

    /// Starts loading the images on either side so moving through the gallery feels instant. Only in the gallery:
    /// these may download from iCloud, and stepping through the list (Command-Right Arrow) mustn't (#120's review).
    private func cacheNeighbors(of index: Int, in rows: [FileRow]) {
        guard viewMode == .gallery else { return }
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

    /// The photo for the Description panel's preview in list view: medium-sized, and only what's on this Mac, so
    /// arrowing through the list never downloads from iCloud (the privacy policy's list of downloads). A photo only in
    /// iCloud shows its small thumbnail instead.
    func previewImage(for id: String) async -> NSImage? {
        if let file = filesByID[id] {
            guard await Task.detached(priority: .userInitiated, operation: { FileAvailability.isOnThisMac(file.url) }).value
            else { return nil }
            return await FileImages.image(file.url, maxPixels: 800)
        }
        guard let asset = assetsByID[id] else { return nil }
        let options = PHImageRequestOptions()
        options.deliveryMode = .highQualityFormat
        options.resizeMode = .fast
        options.isNetworkAccessAllowed = false
        let box = UncheckedBox(value: asset)
        let image = await withCheckedContinuation { continuation in
            imageManager.requestImage(for: box.value, targetSize: CGSize(width: 800, height: 800),
                                      contentMode: .aspectFit, options: options) { image, _ in
                continuation.resume(returning: image)
            }
        }
        if let image { return image }
        return await thumbnail(for: id)
    }

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

    /// Go > Go to Description or Back to Photos (Shift-Command-D): to the Description panel's text, showing the panel if it's
    /// hidden; from there, back to the list or gallery.
    func toggleDescriptionFocus() {
        if !showDescriptionPanel { showDescriptionPanel = true }
        paneRequest = PaneRequest(direction: 0, serial: paneRequest.serial + 1, toggleDescription: true)
    }

    func movePane(by direction: Int) {
        paneRequest = PaneRequest(direction: direction, serial: paneRequest.serial + 1)
    }

    private func announce(_ message: String, queued: Bool = false) {
        announcer(message, queued)
    }

    /// Posts an announcement for VoiceOver.
    static func postAnnouncement(_ message: String, queued: Bool) {
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

/// Apple Intelligence as Describe Photo, Redescribe and describing automatically use it: why it can't be used now,
/// a photo's description from its data, and what to say when describing fails. The library model is given a
/// stand-in in tests (#48), so describing can be tested without Apple Intelligence or the macOS 27 SDK.
struct PhotoDescribing {
    var unavailableReason: @MainActor () -> String?
    var describe: @MainActor (Data) async throws -> String?
    var failureMessage: @MainActor (Error) -> String

    /// Apple's on-device model, on macOS 27 or later; nil before.
    static var appleIntelligence: PhotoDescribing? {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            return PhotoDescribing(unavailableReason: { ImageConversation.unavailableReason },
                                   describe: { try await PhotoDescriber.describe($0) },
                                   failureMessage: { ImageConversation.message(for: $0) })
        }
#endif
        return nil
    }
}

/// What Photo > Rating says and shows, in Windows' words (#20).
enum RatingText {
    static func stars(_ stars: Int) -> String { stars == 0 ? "" : stars == 1 ? "1 star" : "\(stars) stars" }
    static func done(_ stars: Int) -> String { stars == 0 ? "Rating removed" : "Rated \(Self.stars(stars))" }
    static func menuTitle(_ stars: Int) -> String { stars == 0 ? "No Rating" : stars == 1 ? "1 Star" : "\(stars) Stars" }
}
