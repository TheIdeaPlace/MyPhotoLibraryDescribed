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

    var body: some View {
        @Bindable var model = model
        NavigationSplitView {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 180, ideal: 230)
        } detail: {
            switch model.viewMode {
            case .list: FileListView()
            case .gallery: GalleryView()
            }
        }
        .navigationTitle(model.currentTitle)
        .navigationSubtitle(model.statusText)
        .searchable(text: $model.searchText, placement: .toolbar, prompt: "Search names, kinds and dates")
        .searchFocused($searchFocused)
        .onChange(of: model.searchFocusRequest) { searchFocused = true }
        .quickLookPreview($model.previewURL, in: model.previewURLs)
        .sheet(item: $model.infoTarget) { InfoView(target: $0) }
        .sheet(item: $model.captionTarget) { CaptionEditor(target: $0, model: model) }
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

    var body: some View {
        @Bindable var model = model
        Table(of: FileRow.self, selection: $model.selection, sortOrder: $model.sortOrder,
              columnCustomization: $model.columnCustomization) {
            nameColumn
            standardColumns
            extraColumns
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
        .accessibilityLabel(model.currentTitle)
        .overlay { emptyState }
        .safeAreaInset(edge: .bottom, spacing: 0) { StatusBar() }
    }

    // Split up so the compiler can type-check the table.
    @TableColumnBuilder<FileRow, KeyPathComparator<FileRow>>
    private var nameColumn: some TableColumnContent<FileRow, KeyPathComparator<FileRow>> {
        TableColumn("Name", value: \.name) { (row: FileRow) in
            NameCell(row: row, model: model)
        }
        .width(min: 180, ideal: 300)
        .customizationID("name")
        .disabledCustomizationBehavior(.visibility)
    }

    @TableColumnBuilder<FileRow, KeyPathComparator<FileRow>>
    private var standardColumns: some TableColumnContent<FileRow, KeyPathComparator<FileRow>> {
        TableColumn("Date Taken", value: \.sortDate) { (row: FileRow) in Text(row.dateText) }
            .width(min: 120, ideal: 170)
            .customizationID("date")
        TableColumn("Kind", value: \.kind) { (row: FileRow) in Text(row.kind) }
            .width(min: 80, ideal: 130)
            .customizationID("kind")
        TableColumn("Caption", value: \.caption) { (row: FileRow) in Text(row.caption) }
            .width(min: 100, ideal: 220)
            .customizationID("caption")
        TableColumn("Dimensions", value: \.pixelCount) { (row: FileRow) in Text(row.dimensions) }
            .width(min: 80, ideal: 110)
            .customizationID("dimensions")
        TableColumn("Duration", value: \.duration) { (row: FileRow) in Text(row.durationText) }
            .width(min: 60, ideal: 70)
            .customizationID("duration")
        TableColumn("Favorite", value: \.favoriteSort) { (row: FileRow) in Text(row.isFavorite ? "Yes" : "") }
            .width(min: 50, ideal: 60)
            .customizationID("favorite")
    }

    @TableColumnBuilder<FileRow, KeyPathComparator<FileRow>>
    private var extraColumns: some TableColumnContent<FileRow, KeyPathComparator<FileRow>> {
        TableColumn("Keywords", value: \.keywords) { (row: FileRow) in Text(row.keywords) }
            .width(min: 80, ideal: 160)
            .customizationID("keywords")
            .defaultVisibility(.hidden)
        TableColumn("Rating", value: \.rating) { (row: FileRow) in Text(row.ratingText) }
            .width(min: 50, ideal: 70)
            .customizationID("rating")
            .defaultVisibility(.hidden)
        TableColumn("Date Modified", value: \.sortModified) { (row: FileRow) in Text(row.modifiedText) }
            .width(min: 120, ideal: 170)
            .customizationID("modified")
            .defaultVisibility(.hidden)
        TableColumn("File Type", value: \.fileType) { (row: FileRow) in Text(row.fileType) }
            .width(min: 70, ideal: 110)
            .customizationID("fileType")
            .defaultVisibility(.hidden)
        TableColumn("Orientation", value: \.orientation) { (row: FileRow) in Text(row.orientation) }
            .width(min: 70, ideal: 90)
            .customizationID("orientation")
            .defaultVisibility(.hidden)
        TableColumn("Megapixels", value: \.megapixels) { (row: FileRow) in Text(row.megapixelsText) }
            .width(min: 60, ideal: 80)
            .customizationID("megapixels")
            .defaultVisibility(.hidden)
        TableColumn("Location", value: \.locationText) { (row: FileRow) in Text(row.locationText) }
            .width(min: 100, ideal: 150)
            .customizationID("location")
            .defaultVisibility(.hidden)
        TableColumn("Source", value: \.source) { (row: FileRow) in Text(row.source) }
            .width(min: 70, ideal: 100)
            .customizationID("source")
            .defaultVisibility(.hidden)
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
    @FocusState private var focused: Bool

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
        .focused($focused)
        .focusEffectDisabled()
        .onKeyPress(.leftArrow) { model.moveSelection(by: -1); return .handled }
        .onKeyPress(.upArrow) { model.moveSelection(by: -1); return .handled }
        .onKeyPress(.rightArrow) { model.moveSelection(by: 1); return .handled }
        .onKeyPress(.downArrow) { model.moveSelection(by: 1); return .handled }
        .onKeyPress(.space) { model.quickLook(); return .handled }
        .onKeyPress(.return) { model.open(); return .handled }
        .onCommand(#selector(NSText.copy(_:))) { model.copySelection() }
        .onAppear {
            model.ensureGallerySelection()
            focused = true
        }
        .safeAreaInset(edge: .bottom, spacing: 0) { StatusBar() }
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
        // One image element with the same name the list uses, so VoiceOver's
        // image descriptions (VO-Shift-L) look at the full-size picture.
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(row.spokenDescription)
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

private struct StatusBar: View {
    @Environment(LibraryModel.self) private var model

    var body: some View {
        HStack(spacing: 8) {
            if model.isLoading || model.activity != nil || model.nameProgress != nil {
                ProgressView().controlSize(.small)
            }
            Text(model.activity ?? model.nameProgress ?? model.statusText)
            Spacer()
        }
        .font(.callout)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
        .padding(.vertical, 5)
        .background(.bar)
        .accessibilityElement(children: .combine)
    }
}

struct AccessDeniedView: View {
    var body: some View {
        ContentUnavailableView {
            Label("No Access to Photos", systemImage: "photo.badge.exclamationmark")
        } description: {
            Text("Photo Finder needs permission to read your photo library. Turn it on in System Settings, Privacy & Security, Photos, then reopen Photo Finder.")
        } actions: {
            Button("Open Privacy Settings") {
                if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Photos") {
                    NSWorkspace.shared.open(url)
                }
            }
        }
    }
}
