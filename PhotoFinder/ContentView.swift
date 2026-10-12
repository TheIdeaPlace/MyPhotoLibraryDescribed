import QuickLook
import SwiftUI

struct ContentView: View {
    @Environment(LibraryModel.self) private var model

    var body: some View {
        switch model.authState {
        case .unknown:
            ProgressView("Asking for access to your photo library…")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        case .denied:
            AccessDeniedView()
        case .authorized:
            BrowserView()
        }
    }
}

struct BrowserView: View {
    @Environment(LibraryModel.self) private var model
    @FocusState private var searchFocused: Bool
    @FocusState private var pane: Pane?

    var body: some View {
        @Bindable var model = model
        NavigationSplitView {
            SidebarView(pane: $pane)
                .navigationSplitViewColumnWidth(min: 180, ideal: 230)
        } detail: {
            Group {
                switch model.viewMode {
                case .list: FileListView(pane: $pane)
                case .gallery: GalleryView(pane: $pane)
                }
            }
            .safeAreaInset(edge: .bottom, spacing: 0) { StatusBar(pane: $pane) }
        }
        .navigationTitle(model.currentTitle)
        .navigationSubtitle(model.statusText)
        .searchable(text: $model.searchText, placement: .toolbar, prompt: "Search names, kinds and dates")
        .searchFocused($searchFocused)
        .onChange(of: model.searchFocusRequest) { searchFocused = true }
        // Go > Next Pane (F6) and Previous Pane (Shift-F6): sidebar, list or gallery, Search, status bar, and round.
        .onChange(of: model.paneRequest) {
            let target = Pane.next(from: searchFocused ? .search : pane, by: model.paneRequest.direction)
            if target == .search {
                searchFocused = true
            } else {
                searchFocused = false
                pane = target
            }
        }
        .quickLookPreview($model.previewURL, in: model.previewURLs)
        .sheet(item: $model.infoTarget) { InfoView(target: $0) }
        .sheet(item: $model.captionTarget) { CaptionEditor(target: $0, model: model) }
        .sheet(isPresented: $model.columnsSheetShown) { ColumnsSheet(model: model) }
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
        HStack {
            Label(item.title, systemImage: item.systemImage)
            Spacer()
            Text(item.count.formatted())
                .monospacedDigit()
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(item.title), \(item.count == 1 ? "1 item" : "\(item.count.formatted()) items")")
        .accessibilityAddTraits(.isStaticText)
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
        // Built from the chosen columns, in the chosen order (View > Columns), which is also the order
        // VoiceOver reads a row's cells in.
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
    }

    @ViewBuilder private func cell(_ column: String, _ row: FileRow) -> some View {
        if column == ColumnOrder.name {
            NameCell(row: row, model: model)
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
        // One image element named just for the photo, so arrowing says which photo and its description follows,
        // and VoiceOver's image descriptions (VO-Shift-L) look at the full-size picture. The rest is in the footer.
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(row.name)
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
                Text([row.kind, row.dateText, row.dimensions].filter { !$0.isEmpty }.joined(separator: " · "))
                    .foregroundStyle(.secondary)
                if !row.caption.isEmpty {
                    Text(row.caption)
                }
                // The saved description, as Windows' gallery shows it. Spoken by Describe Photos Automatically, so
                // the image itself is still named just for the photo.
                if let description = model.savedDescription(for: row.id) {
                    Text(description)
                        .lineLimit(4)
                        .fixedSize(horizontal: false, vertical: true)
                        .help(description)
                }
            }
            .textSelection(.enabled)
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
            Text("Captions are saved to your Photos library and sync with iCloud Photos.")
                .font(.caption)
                .foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    model.saveCaption(text, for: target)
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding()
        .frame(width: 440)
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
        case .background: model.backgroundProgress
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
                Text(text(section) ?? "")
                    .focusable()
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

struct AccessDeniedView: View {
    var body: some View {
        ContentUnavailableView {
            Label("No Access to Photos", systemImage: "photo.badge.exclamationmark")
        } description: {
            Text("\(AppInfo.name) needs permission to read your photo library. Turn it on in System Settings, Privacy & Security, Photos, then reopen \(AppInfo.name).")
        } actions: {
            Button("Open Privacy Settings") {
                if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Photos") {
                    NSWorkspace.shared.open(url)
                }
            }
        }
    }
}
