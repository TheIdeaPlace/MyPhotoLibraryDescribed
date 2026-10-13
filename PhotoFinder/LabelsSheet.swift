import SwiftUI

/// View > Labels (#38): what's read for each photo, in two tabs.
/// - **List:** which columns the list shows and in what order, which is also the order VoiceOver reads each row's
///   cells in (it was View > Columns).
/// - **Gallery:** what's said, in order, when you move to a photo in the gallery. Position only by default.
///
/// Both tabs are the same editor (`OrderListEditor`), the same design as the Windows app's (and The Chat Place's):
/// the items not shown, the ones shown in the order they're read, Add, Remove, Move Up, Move Down, Move to Top,
/// Move to Bottom and Reset to Default, and a preview of how the selected photo reads. OK keeps both tabs' choices;
/// Cancel (or Escape) leaves them.
struct LabelsSheet: View {
    enum Tab: Hashable { case list, gallery }

    let model: LibraryModel
    @Environment(\.dismiss) private var dismiss
    @State private var tab: Tab
    @State private var columns: OrderEditor
    @State private var gallery: OrderEditor
    @FocusState private var focus: OrderField?

    init(model: LibraryModel) {
        self.model = model
        // Opens on the tab for the view you're in.
        _tab = State(initialValue: model.viewMode == .gallery ? .gallery : .list)
        _columns = State(initialValue: OrderEditor(.columns, shown: model.columnOrder))
        _gallery = State(initialValue: OrderEditor(.gallery, shown: model.galleryLabels.map(\.rawValue)))
    }

    private var sample: FileRow? {
        model.focusedRow.flatMap { $0.isContainer ? nil : $0 } ?? model.rows.first { !$0.isContainer }
    }

    private var listPreview: String { sample?.line(for: columns.shown) ?? "There's no photo here to preview." }

    private var galleryPreview: String {
        guard let sample else { return "There's no photo here to preview." }
        let rows = model.visibleRows
        let index = rows.firstIndex { $0.id == sample.id } ?? 0
        return GalleryLabels.text(for: sample, position: (index + 1, max(rows.count, 1)),
                                  items: GalleryLabels.clean(gallery.shown))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            TabView(selection: $tab) {
                OrderListEditor(editor: $columns, text: .columns, preview: listPreview, focus: $focus,
                                available: .listAvailable, shown: .listShown, isActive: tab == .list)
                    .padding()
                    .tabItem { Text("List") }
                    .tag(Tab.list)
                OrderListEditor(editor: $gallery, text: .gallery, preview: galleryPreview, focus: $focus,
                                available: .galleryAvailable, shown: .galleryShown, isActive: tab == .gallery)
                    .padding()
                    .tabItem { Text("Gallery") }
                    .tag(Tab.gallery)
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("OK") {
                    if columns.shown != model.columnOrder { model.setColumnOrder(columns.shown) }
                    let labels = GalleryLabels.clean(gallery.shown)
                    if labels != model.galleryLabels { model.galleryLabels = labels }
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding()
        .frame(minWidth: 660, minHeight: 540)
        .closesWithKeyboard(escape: false) { dismiss() }
        // Set as the sheet's default rather than on appear, which can run before the sheet has a window.
        .defaultFocus($focus, tab == .gallery ? .galleryShown : .listShown)
    }
}

/// Which list has focus in the Labels sheet.
enum OrderField: Hashable { case listAvailable, listShown, galleryAvailable, galleryShown }

/// The words one tab of the Labels sheet uses.
struct OrderListText {
    let explanation: String
    /// The lists' names, read by VoiceOver and shown above them with a colon.
    let availableTitle: String
    let shownTitle: String
    let everyShown: String
    let availableKeys: String
    let shownKeys: String
    let previewTitle: String

    static let columns = OrderListText(
        explanation: "Choose which columns the list shows, and in what order. VoiceOver reads each photo in this order too: put Caption or Date Taken first to hear it before the file name.",
        availableTitle: "Available columns",
        shownTitle: "Shown columns, in the order they're read",
        everyShown: "Every column is shown.",
        availableKeys: "Return adds the selected column.",
        shownKeys: "Delete or Return removes the selected column. Option-Up Arrow and Option-Down Arrow move it; Option-Home and Option-End move it to the top or bottom.",
        previewTitle: "Preview of the selected photo's line")

    static let gallery = OrderListText(
        explanation: "Choose what's read, and in what order, when you move to a photo in the gallery. The description follows after a pause when Describe Photos Automatically is on.",
        availableTitle: "Available labels",
        shownTitle: "Shown labels, in the order they're read",
        everyShown: "Every label is shown.",
        availableKeys: "Return adds the selected label.",
        shownKeys: "Delete or Return removes the selected label. Option-Up Arrow and Option-Down Arrow move it; Option-Home and Option-End move it to the top or bottom.",
        previewTitle: "Preview of how the selected photo reads in the gallery")
}

/// Available and Shown lists with Add, Remove, the Move buttons, Reset to Default and a preview: one tab of the Labels
/// sheet.
///
/// Keys: Return (or a double-click) in Available adds, and in Shown removes; Delete in Shown removes.
/// Option-Up Arrow, Option-Down Arrow, Option-Home and Option-End move the selected shown item: they're the
/// Move buttons' keyboard shortcuts, which work wherever focus is in the sheet, rather than key handlers on the
/// list, which a Mac list may never pass the keys to. Every action is announced, and the moved item stays
/// selected, so arrowing on reads its new neighbours.
struct OrderListEditor: View {
    @Binding var editor: OrderEditor
    let text: OrderListText
    let preview: String
    var focus: FocusState<OrderField?>.Binding
    let available: OrderField
    let shown: OrderField
    /// Whether this is the tab showing.
    let isActive: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(text.explanation)
                .fixedSize(horizontal: false, vertical: true)
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    // The list carries this as its label, so VoiceOver doesn't hear it twice.
                    Text("\(text.availableTitle):").accessibilityHidden(true)
                    List(selection: $editor.availableSelection) {
                        if editor.notShown.isEmpty {
                            // An empty list reads as nothing at all to VoiceOver.
                            Text(text.everyShown).foregroundStyle(.secondary)
                        }
                        ForEach(editor.notShown, id: \.self) { id in
                            Text(editor.choice.title(id)).tag(id as String?)
                        }
                    }
                    // Return and double-click: a list's primary action, the native way to act on its row.
                    .contextMenu(forSelectionType: String.self, menu: { _ in }, primaryAction: { _ in say(editor.add()) })
                    .focused(focus, equals: available)
                    .accessibilityLabel(text.availableTitle)
                    .accessibilityHint("\(text.explanation) \(text.availableKeys)")
                }
                VStack(alignment: .leading, spacing: 4) {
                    Text("\(text.shownTitle):").accessibilityHidden(true)
                    List(selection: $editor.shownSelection) {
                        ForEach(editor.shown, id: \.self) { id in
                            Text(editor.choice.title(id)).tag(id as String?)
                        }
                    }
                    .contextMenu(forSelectionType: String.self, menu: { _ in }, primaryAction: { _ in say(editor.remove()) })
                    .onDeleteCommand { say(editor.remove()) }
                    .focused(focus, equals: shown)
                    .accessibilityLabel(text.shownTitle)
                    .accessibilityHint("\(text.explanation) \(text.shownKeys)")
                }
            }
            .frame(minHeight: 220)
            // Always enabled, so the order you meet them in never changes; each says when there's nothing it can do.
            HStack {
                Button("Add") { say(editor.add()) }
                Button("Remove") { say(editor.remove()) }
                Button("Move Up") { say(editor.move(by: -1)) }
                    .keyboardShortcut(shortcut(.upArrow))
                Button("Move Down") { say(editor.move(by: 1)) }
                    .keyboardShortcut(shortcut(.downArrow))
                Button("Move to Top") { say(editor.moveToEnd(top: true)) }
                    .keyboardShortcut(shortcut(.home))
                Button("Move to Bottom") { say(editor.moveToEnd(top: false)) }
                    .keyboardShortcut(shortcut(.end))
                Button("Reset to Default") { say(editor.reset()) }
            }
            VStack(alignment: .leading, spacing: 4) {
                Text("\(text.previewTitle):").accessibilityHidden(true)
                Text(preview)
                    .textSelection(.enabled)
                    .accessibilityLabel(text.previewTitle)
                    .accessibilityValue(preview)
            }
        }
    }

    /// Option with the key, only on the tab showing, so a tab that's out of sight can never take the key instead.
    private func shortcut(_ key: KeyEquivalent) -> KeyboardShortcut? {
        isActive ? KeyboardShortcut(key, modifiers: .option) : nil
    }

    private func say(_ text: String) {
        AccessibilityNotification.Announcement(text).post()
    }
}

/// What one tab of the Labels sheet chooses from: the list's columns, or what the gallery reads.
struct OrderChoice {
    /// Every item, in its original order.
    let all: [String]
    let defaultOrder: [String]
    /// An item that can't be removed (the list's Name), if any.
    let required: String?
    let singular: String
    let plural: String
    let title: (String) -> String
    /// A usable order from a saved or edited one.
    let clean: ([String]) -> [String]
    let resetMessage: (_ shown: [String]) -> String

    static let columns = OrderChoice(
        all: ColumnOrder.all.map(\.id),
        defaultOrder: ColumnOrder.defaultOrder,
        required: ColumnOrder.name,
        singular: "column", plural: "columns",
        title: ColumnOrder.title,
        clean: { ColumnOrder.clean($0) },
        resetMessage: { "Columns reset to the default: \($0.count) shown, Name first." })

    static let gallery = OrderChoice(
        all: GalleryItem.allCases.map(\.rawValue),
        defaultOrder: GalleryLabels.defaultOrder.map(\.rawValue),
        required: nil,
        singular: "label", plural: "labels",
        title: { GalleryItem(rawValue: $0)?.title ?? $0 },
        clean: { GalleryLabels.clean($0).map(\.rawValue) },
        resetMessage: { shown in
            "Labels reset to the default: \(shown.compactMap { GalleryItem(rawValue: $0)?.title }.joined(separator: ", "))."
        })
}

/// One tab's state and actions, apart from the view so they can be tested. Each action returns what to announce.
struct OrderEditor {
    let choice: OrderChoice
    private(set) var shown: [String]
    var shownSelection: String?
    var availableSelection: String?

    init(_ choice: OrderChoice, shown: [String]) {
        self.choice = choice
        self.shown = choice.clean(shown)
        shownSelection = self.shown.first
        availableSelection = notShown.first
    }

    var notShown: [String] { choice.all.filter { !shown.contains($0) } }

    private func position(_ index: Int) -> String { "\(index + 1) of \(shown.count)" }

    private var noneSelected: String { "No \(choice.singular) selected." }

    /// Adds the selected available item at the end, and selects the next available one to add more.
    mutating func add() -> String {
        let rest = notShown
        guard !rest.isEmpty else { return "Every \(choice.singular) is shown already." }
        guard let id = availableSelection, let index = rest.firstIndex(of: id) else { return noneSelected }
        shown.append(id)
        shownSelection = id
        let remaining = notShown
        availableSelection = remaining.isEmpty ? nil : remaining[min(index, remaining.count - 1)]
        return "\(choice.title(id)) added, \(position(shown.count - 1))."
    }

    mutating func remove() -> String {
        guard let id = shownSelection, let index = shown.firstIndex(of: id) else { return noneSelected }
        guard id != choice.required else { return "\(choice.title(id)) is always shown. It can be moved." }
        // Something has to be read: the last item stays until another is added.
        guard shown.count > 1 else { return "\(choice.title(id)) is the only \(choice.singular) shown. Add another before removing it." }
        shown.remove(at: index)
        shownSelection = shown[min(index, shown.count - 1)]
        if availableSelection == nil { availableSelection = id }
        return "\(choice.title(id)) removed. \(shown.count == 1 ? "1 \(choice.singular)" : "\(shown.count) \(choice.plural)") shown."
    }

    mutating func move(by step: Int) -> String {
        guard let id = shownSelection, let index = shown.firstIndex(of: id) else { return noneSelected }
        return moveTo(index, index + step, how: step < 0 ? "up" : "down", end: step < 0 ? "top" : "bottom")
    }

    mutating func moveToEnd(top: Bool) -> String {
        guard let id = shownSelection, let index = shown.firstIndex(of: id) else { return noneSelected }
        let end = top ? "top" : "bottom"
        return moveTo(index, top ? 0 : shown.count - 1, how: "to \(end)", end: end)
    }

    private mutating func moveTo(_ index: Int, _ target: Int, how: String, end: String) -> String {
        let id = shown[index]
        guard index != target, shown.indices.contains(target) else { return "\(choice.title(id)) is already at the \(end)." }
        shown.remove(at: index)
        shown.insert(id, at: target)
        shownSelection = id
        return "\(choice.title(id)) moved \(how), \(position(target))."
    }

    mutating func reset() -> String {
        shown = choice.defaultOrder
        shownSelection = shown.first
        availableSelection = notShown.first
        return choice.resetMessage(shown)
    }
}
