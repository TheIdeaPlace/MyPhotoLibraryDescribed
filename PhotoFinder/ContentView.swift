import QuickLook
import SwiftUI

struct ContentView: View {
    @Environment(LibraryModel.self) private var model

    var body: some View {
        switch model.authState {
        case .unknown:
            ProgressView("Asking for access to your photo library…")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        case .denied, .authorized:
            // Without Photos access the browser still opens, for added folders; the sidebar's "Photos library: no
            // access" row says how to give it (#47).
            BrowserView()
        }
    }
}

struct BrowserView: View {
    @Environment(LibraryModel.self) private var model
    @FocusState private var searchFocused: Bool
    @FocusState private var pane: Pane?
    /// Where F6 is taking you in the status bar, while its sections become focusable.
    @State private var pendingStatusFocus: Pane?

    /// Moves focus into the status bar once its sections can take it: a few tries, a moment apart, stopping as soon
    /// as focus is there or another move (F6 again, a click) replaced this one.
    private func focusStatusBar(_ target: Pane) {
        Task { @MainActor in
            for _ in 0..<10 {
                try? await Task.sleep(for: .milliseconds(30))
                guard pendingStatusFocus == target else { return }
                // Again each time: a try SwiftUI turned down counts as leaving the status bar, which makes it
                // unfocusable.
                model.statusBarReachable = true
                pane = target
                try? await Task.sleep(for: .milliseconds(30))
                if pane == target { break }
            }
            guard pendingStatusFocus == target else { return }
            pendingStatusFocus = nil
            // It never took: out of the Tab order again.
            if case .status? = pane {} else { model.statusBarReachable = false }
        }
    }

    var body: some View {
        @Bindable var model = model
        NavigationSplitView {
            SidebarView(pane: $pane)
                .navigationSplitViewColumnWidth(min: 180, ideal: 230)
        } detail: {
            Group {
                if model.showingPhotosNoAccess {
                    PhotosAccessView(pane: $pane)
                } else {
                    switch model.viewMode {
                    case .list: FileListView(pane: $pane)
                    case .gallery: GalleryView(pane: $pane)
                    }
                }
            }
            .safeAreaInset(edge: .bottom, spacing: 0) { StatusBar(pane: $pane) }
            .inspector(isPresented: $model.showDescriptionPanel) {
                DescriptionPanel(pane: $pane)
                    .inspectorColumnWidth(min: 220, ideal: 300, max: 520)
            }
        }
        .navigationTitle(model.windowTitle)
        // Kept although the status bar says the same: with a subtitle the title is drawn smaller, and without it the
        // mode ("Read-Only Mode") was cut off (#35's look).
        .navigationSubtitle(model.statusText)
        .searchable(text: $model.searchText, placement: .toolbar, prompt: "Search names, kinds and dates")
        .searchFocused($searchFocused)
        .onChange(of: model.searchFocusRequest) { searchFocused = true }
        // Go > Next Pane (F6) and Previous Pane (Shift-F6): sidebar, list or gallery, Description, Search, status bar,
        // and round.
        .onChange(of: model.paneRequest) {
            let toggled: Pane? = model.paneRequest.toggleDescription ? (pane == .description ? .content : .description) : nil
            let target = toggled ?? model.paneRequest.target ?? Pane.next(from: searchFocused ? .search : pane,
                                                               by: model.paneRequest.direction,
                                                               showingDescription: model.showDescriptionPanel)
            if target == .search {
                pendingStatusFocus = nil
                searchFocused = true
            } else if case .status = target {
                searchFocused = false
                // Focusable first, then focus moves once SwiftUI has made the sections focus targets. Setting focus in
                // the same update did nothing (Kelly, 13 Oct: F6 never reached the status bar), so it's set a moment
                // later and again until it takes.
                model.statusBarReachable = true
                pendingStatusFocus = target
                focusStatusBar(target)
            } else {
                pendingStatusFocus = nil
                searchFocused = false
                pane = target
                // The Description panel may only just be appearing (Shift-Command-D shows it): tried again a
                // moment later if focus didn't take.
                if target == .description {
                    let serial = model.paneRequest.serial
                    Task { @MainActor in
                        for _ in 0..<5 where pane != .description {
                            try? await Task.sleep(for: .milliseconds(50))
                            // Another move (F6, Shift-Command-D again) since: that one wins.
                            guard model.paneRequest.serial == serial else { return }
                            pane = .description
                        }
                    }
                }
            }
        }
        .quickLookPreview($model.previewURL, in: model.previewURLs)
        .sheet(item: $model.infoTarget) { InfoView(target: $0) }
        .sheet(item: $model.captionTarget) { CaptionEditor(target: $0, model: model) }
        .sheet(isPresented: $model.labelsSheetShown) { LabelsSheet(model: model) }
        .alert("Something went wrong", isPresented: Binding(
            get: { model.errorMessage != nil },
            set: { if !$0 { model.errorMessage = nil } }
        )) {
            Button("OK") { model.errorMessage = nil }
        } message: {
            Text(model.errorMessage ?? "")
        }
    }
}

struct SidebarView: View {
    @Environment(LibraryModel.self) private var model
    var pane: FocusState<Pane?>.Binding

    var body: some View {
        @Bindable var model = model
        List(selection: $model.selectedSourceID) {
            ForEach(model.sections) { section in
                Section(section.title) {
                    ForEach(section.items) { SidebarNode(item: $0) }
                }
            }
        }
        .listStyle(.sidebar)
        // Return or a double-click on "Photos library: no access" opens Privacy Settings (#47); other rows are
        // unchanged.
        // `String?`, the rows' tag type (the selection is optional): a plain `String` never matches them (#142's review).
        .contextMenu(forSelectionType: String?.self) { ids in
            if ids.contains(SidebarID.photosNoAccess) {
                Button("Open Privacy Settings") { model.openPhotosPrivacySettings() }
            }
        } primaryAction: { ids in
            if ids.contains(SidebarID.photosNoAccess) { model.openPhotosPrivacySettings() }
        }
        // Delete on an added folder removes it from the library (after asking), as on Windows (#36).
        .onDeleteCommand { model.deleteInSidebar() }
        .focused(pane, equals: .sidebar)
        .accessibilityLabel("Sidebar")
    }
}

private struct SidebarNode: View {
    let item: SidebarItem
    @Environment(LibraryModel.self) private var model

    var body: some View {
        if let children = item.children {
            DisclosureGroup(isExpanded: expanded) {
                ForEach(children) { SidebarNode(item: $0) }
            } label: {
                label
            }
        } else {
            label
        }
    }

    private var label: some View {
        // Not .badge(): VoiceOver reads a badge as the row's value, so the sidebar sounds like a list of numbers.
        // The no-access row has no count: it's read as its name only.
        let noAccess = item.kind == .photosNoAccess
        return HStack {
            Label(item.title, systemImage: item.systemImage)
            Spacer()
            if !noAccess {
                Text(item.count.formatted())
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(noAccess ? item.title
                            : "\(item.title), \(item.count == 1 ? "1 item" : "\(item.count.formatted()) items")")
        .accessibilityAddTraits(.isStaticText)
        .accessibilityHint(noAccess ? "Return opens Privacy Settings" : "")
        .tag(item.id as String?)
    }

    private var expanded: Binding<Bool> {
        Binding(
            get: { model.expandedIDs.contains(item.id) },
            set: { isOpen in
                if isOpen { model.expandedIDs.insert(item.id) } else { model.expandedIDs.remove(item.id) }
            }
        )
    }
}

struct FileListView: View {
    @Environment(LibraryModel.self) private var model
    var pane: FocusState<Pane?>.Binding

    var body: some View {
        @Bindable var model = model
        // Built from the chosen columns, in the chosen order (View > Labels, List tab), which is also the order
        // VoiceOver reads a row's cells in.
        ScrollViewReader { proxy in
            Table(of: FileRow.self, selection: $model.selection, sortOrder: $model.sortOrder) {
                TableColumnForEach(model.shownColumns) { column in
                    TableColumn(column.title, sortUsing: ColumnOrder.comparator(column.id)) { (row: FileRow) in
                        cell(column.id, row)
                    }
                    .width(min: ColumnOrder.width(column.id).min, ideal: ColumnOrder.width(column.id).ideal)
                }
            } rows: {
                ForEach(model.visibleRows) { row in
                    TableRow(row)
                        .itemProvider { model.itemProvider(for: row) }
                }
            }
            .contextMenu(forSelectionType: String.self) { ids in
                if !ids.isEmpty {
                    Button("Open") { model.open(ids) }
                    Button("Quick Look") { model.quickLook(ids) }
                    Button("Get Info") { model.showInfo(ids) }
                    Button("Edit Caption…") { model.editCaption(ids) }
                    Divider()
                    Button("Copy") { model.copySelection(ids) }
                    Button("Copy Description") { model.copyDescription(ids) }
                    Button("Export…") { model.exportSelection(ids) }
                    Divider()
                    Button("Toggle Favorite") { model.toggleFavorite(ids) }
                }
            } primaryAction: { ids in
                model.open(ids)
            }
            .onKeyPress(.space) {
                model.quickLook()
                return .handled
            }
            .onKeyPress(.return) {
                model.open()
                return .handled
            }
            .onCommand(#selector(NSText.copy(_:))) { model.copySelection() }
            .focused(pane, equals: .content)
            .accessibilityLabel(model.currentTitle)
            .overlay { emptyState }
            // Next Photo and Previous Photo change the selection without the list: it's scrolled into view.
            .onChange(of: model.scrollRequest) { if let id = model.scrollRequest.id { proxy.scrollTo(id) } }
        }
    }

    @ViewBuilder private func cell(_ column: String, _ row: FileRow) -> some View {
        if column == ColumnOrder.name {
            NameCell(row: row, model: model)
        } else if column == "location" {
            // Place Taken looks up the names of the places showing (#172), as their cells come into view, and when
            // Look Up Place Names is turned on with them in view.
            Text(row.text(for: column))
                .task(id: model.placeNamesShowing ? row.placeKey : nil) { model.placeCellAppeared(row) }
                .onDisappear { model.placeCellDisappeared(row) }
        } else {
            Text(row.text(for: column))
        }
    }

    @ViewBuilder private var emptyState: some View {
        if model.isLoading && model.rows.isEmpty {
            ProgressView("Loading…")
        } else if !model.isLoading && model.visibleRows.isEmpty {
            if model.searchText.isEmpty {
                ContentUnavailableView("No Items", systemImage: "photo", description: Text("There is nothing here."))
            } else {
                ContentUnavailableView.search(text: model.searchText)
            }
        }
    }
}

private struct NameCell: View {
    let row: FileRow
    // Passed in directly: Table cells don't reliably inherit @Observable environment objects.
    let model: LibraryModel

    var body: some View {
        HStack(spacing: 6) {
            if row.isContainer {
                Image(systemName: row.symbol)
                    .frame(width: 22, height: 22)
                    .accessibilityHidden(true)
            } else {
                ThumbnailView(id: row.id, model: model)
            }
            Text(row.name)
                .lineLimit(1)
                .truncationMode(.middle)
        }
    }
}

private struct ThumbnailView: View {
    let id: String
    let model: LibraryModel
    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
            } else {
                Rectangle().fill(.quaternary)
            }
        }
        .frame(width: 22, height: 22)
        .clipShape(RoundedRectangle(cornerRadius: 3))
        .accessibilityHidden(true)
        .task(id: id) { image = await model.thumbnail(for: id) }
    }
}

struct GalleryView: View {
    @Environment(LibraryModel.self) private var model
    var pane: FocusState<Pane?>.Binding

    var body: some View {
        VStack(spacing: 0) {
            if let row = model.focusedRow {
                GalleryImage(row: row, model: model)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .padding()
                Divider()
                GalleryFooter(row: row, model: model)
            } else if model.isLoading {
                ProgressView("Loading…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ContentUnavailableView("No Items", systemImage: "photo", description: Text("There is nothing here."))
            }
        }
        .focusable()
        .focused(pane, equals: .content)
        .focusEffectDisabled()
        .onKeyPress(.leftArrow) { model.moveSelection(by: -1); return .handled }
        .onKeyPress(.upArrow) { model.moveSelection(by: -1); return .handled }
        .onKeyPress(.rightArrow) { model.moveSelection(by: 1); return .handled }
        .onKeyPress(.downArrow) { model.moveSelection(by: 1); return .handled }
        .onKeyPress(.home) { model.moveSelectionToEnd(last: false); return .handled }
        .onKeyPress(.end) { model.moveSelectionToEnd(last: true); return .handled }
        .onKeyPress(.space) { model.quickLook(); return .handled }
        .onKeyPress(.return) { model.open(); return .handled }
        .onCommand(#selector(NSText.copy(_:))) { model.copySelection() }
        .onAppear {
            model.ensureGallerySelection()
            pane.wrappedValue = .content
        }
    }
}

private struct GalleryImage: View {
    let row: FileRow
    let model: LibraryModel
    @State private var image: NSImage?
    @State private var loadedID: String?

    private var label: String {
        guard let position = model.galleryPosition else { return row.name }
        return model.galleryText(for: row, at: position.index, of: position.count)
    }

    var body: some View {
        Group {
            if row.isContainer {
                Image(systemName: row.symbol)
                    .resizable()
                    .scaledToFit()
                    .frame(maxWidth: 160, maxHeight: 160)
                    .foregroundStyle(.secondary)
            } else if let image, loadedID == row.id {
                Image(nsImage: image)
                    .resizable()
                    .scaledToFit()
            } else {
                ProgressView("Loading image…")
            }
        }
        // One image element, so VoiceOver's image descriptions (VO-Shift-L) look at the full-size picture. It's named
        // with the same words the move announcement says, the items chosen in View > Labels > Gallery (#38), and
        // nothing more. Arrowing keeps focus where it is, and VoiceOver doesn't read a label again when it changes
        // under it, so a move is heard once, as the announcement. Landing on the image afresh (F6, VO-arrows) reads
        // the same chosen words, never a name that wasn't chosen. The rest is in the footer.
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(label)
        .accessibilityAddTraits(.isImage)
        .task(id: row.id) {
            guard !row.isContainer else { return }
            let loaded = await model.fullImage(for: row.id)
            image = loaded
            loadedID = row.id
        }
    }
}

private struct GalleryFooter: View {
    let row: FileRow
    let model: LibraryModel

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(row.name)
                    .font(.headline)
                // Kind and size on one line, the date on its own, so a long date doesn't break mid-phrase (#35's look).
                let details = [row.kind, row.dimensions].filter { !$0.isEmpty }.joined(separator: " · ")
                if !details.isEmpty {
                    Text(details)
                        .foregroundStyle(.secondary)
                }
                if !row.dateText.isEmpty {
                    Text(row.dateText)
                        .foregroundStyle(.secondary)
                }
                if !row.caption.isEmpty {
                    Text(row.caption)
                }
                // The description itself is in the Description panel (#50); shown here too only when that's hidden.
                if !model.showDescriptionPanel, let description = model.savedDescription(for: row.id) {
                    Text(description)
                        .lineLimit(4)
                        .fixedSize(horizontal: false, vertical: true)
                        .help(description)
                }
            }
            .textSelection(.enabled)
            // The name and details get the room before the buttons do.
            .layoutPriority(1)
            Spacer()
            let position = model.galleryPosition
            Button("Previous", systemImage: "chevron.left") { model.moveSelection(by: -1) }
                .disabled((position?.index ?? 0) == 0)
            if let position {
                Text("\(position.index + 1) of \(position.count)")
                    .monospacedDigit()
            }
            Button("Next", systemImage: "chevron.right") { model.moveSelection(by: 1) }
                .disabled(position.map { $0.index + 1 >= $0.count } ?? true)
        }
        .padding(.horizontal)
        .padding(.vertical, 8)
    }
}

private struct CaptionEditor: View {
    let target: CaptionTarget
    let model: LibraryModel
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(target.assetIDs.count == 1 ? "Edit Caption" : "Edit Caption for \(target.assetIDs.count) Items")
                .font(.headline)
                .accessibilityAddTraits(.isHeader)
            TextField("Caption", text: $text, axis: .vertical)
                .lineLimit(3...8)
                .textFieldStyle(.roundedBorder)
            if target.assetIDs.count > 1 {
                Text("This replaces the caption on all \(target.assetIDs.count) items.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Text(target.inFiles ? "Captions are saved in the file, where other apps can read them."
                 : "Captions are saved to your Photos library and sync with iCloud Photos.")
                .font(.caption)
                .foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    // Saving an empty field for several items removes all their captions: ask, since the field
                    // starts empty and pressing Return straight away would otherwise do it silently.
                    if target.assetIDs.count > 1, text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        let alert = NSAlert()
                        alert.messageText = "Remove the captions of \(target.assetIDs.count) items?"
                        alert.informativeText = "The caption field is empty, so saving removes every one of their captions."
                        alert.addButton(withTitle: "Remove Captions")
                        alert.addButton(withTitle: "Cancel")
                        guard alert.runModal() == .alertFirstButtonReturn else { return }
                    }
                    model.saveCaption(text, for: target)
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding()
        .frame(width: 440)
        .closesWithKeyboard(escape: false) { dismiss() }
        .onAppear { text = target.initialText }
    }
}

/// The last stop in the F6 loop. Each section is its own focus stop, Left and Right Arrow move between them
/// (wrapping), and Escape goes back to the list or gallery. Sections with nothing to say aren't shown.
private struct StatusBar: View {
    @Environment(LibraryModel.self) private var model
    var pane: FocusState<Pane?>.Binding

    private func text(_ section: StatusSection) -> String? {
        switch section {
        case .status: model.statusText
        case .activity: model.activity ?? model.nameProgress
        case .background: model.backgroundStatus
        }
    }

    private var shown: [StatusSection] { StatusSection.allCases.filter { !(text($0) ?? "").isEmpty } }

    var body: some View {
        HStack(spacing: 12) {
            if model.isLoading || model.activity != nil || model.nameProgress != nil || model.backgroundProgress != nil {
                ProgressView().controlSize(.small)
                    .accessibilityHidden(true)
            }
            ForEach(shown, id: \.self) { section in
                // `.edit`: a view that only has `.activate` takes focus on macOS only with Keyboard navigation on, so
                // this makes sure F6 can land here either way.
                Text(text(section) ?? "")
                    .focusable(model.statusBarReachable, interactions: .edit)
                    .focused(pane, equals: .status(section))
            }
            Spacer()
        }
        .font(.callout)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
        .padding(.vertical, 5)
        .background(.bar)
        .onKeyPress(.leftArrow) { move(-1) }
        .onKeyPress(.rightArrow) { move(1) }
        .onKeyPress(.escape) {
            guard case .status = pane.wrappedValue else { return .ignored }
            pane.wrappedValue = .content
            return .handled
        }
        // The section you're on went away (an export finished): stay in the status bar. SwiftUI may clear focus
        // before `shown` changes, so this watches focus too.
        .onChange(of: shown) {
            if case let .status(section)? = pane.wrappedValue, !shown.contains(section) { pane.wrappedValue = .status(.status) }
        }
        .onChange(of: pane.wrappedValue) { old, new in
            if new == nil, case let .status(section)? = old, !shown.contains(section) { pane.wrappedValue = .status(.status) }
            // Left the status bar (F6, Escape, a click, focus lost): out of the Tab order again. Not when the section
            // you were on just went away, which keeps you in the status bar (above).
            if case .status? = new {} else if case let .status(section)? = old, new != nil || shown.contains(section) {
                model.statusBarReachable = false
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Status bar")
    }

    private func move(_ direction: Int) -> KeyPress.Result {
        guard case let .status(section)? = pane.wrappedValue else { return .ignored }
        pane.wrappedValue = .status(StatusSection.next(from: section, by: direction, shown: shown))
        return .handled
    }
}

/// Shown in place of the list or gallery while the sidebar's "Photos library: no access" row is chosen (#47): the
/// explanation and Open Privacy Settings that used to block the whole window. It's the F6 loop's list-or-gallery stop:
/// Return or Space opens Privacy Settings, and VoiceOver can read the explanation and the button inside it.
struct PhotosAccessView: View {
    @Environment(LibraryModel.self) private var model
    var pane: FocusState<Pane?>.Binding

    static let explanation = "\(AppInfo.name) needs permission to read your Photos library. Turn it on in System Settings, Privacy & Security, Photos. Folders you add with File > Add Folder to Library work without it."

    var body: some View {
        ContentUnavailableView {
            Label("No Access to Photos", systemImage: "photo.badge.exclamationmark")
        } description: {
            Text(Self.explanation)
        } actions: {
            Button("Open Privacy Settings") { model.openPhotosPrivacySettings() }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .focusable()
        .focused(pane, equals: .content)
        .onKeyPress(.return) { model.openPhotosPrivacySettings(); return .handled }
        .onKeyPress(.space) { model.openPhotosPrivacySettings(); return .handled }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("No access to Photos")
        .accessibilityHint("Return opens Privacy Settings")
    }
}
