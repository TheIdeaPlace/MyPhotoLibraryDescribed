import SwiftUI

/// View > Columns: which columns the list shows and in what order, which is also the order VoiceOver reads each
/// row's cells in. The same design as the Windows app's (and The Chat Place's): the columns not shown, the ones
/// shown in the order they're read, Add, Remove, Move Up, Move Down, Move to Top, Move to Bottom and Reset to
/// Default, and a preview of the selected photo's line. OK keeps the choice; Cancel (or Escape) leaves it.
///
/// Keys as well as buttons: Return in Available adds, Delete in Shown removes, and Option-Up Arrow,
/// Option-Down Arrow, Option-Home and Option-End in Shown move. Every action is announced, and the moved column
/// stays selected, so arrowing on reads its new neighbours.
struct ColumnsSheet: View {
    let model: LibraryModel
    @Environment(\.dismiss) private var dismiss
    @State private var editor: ColumnsEditor
    @FocusState private var focus: Field?

    private enum Field { case available, shown }

    private static let explanation = "Choose which columns the list shows, and in what order. VoiceOver reads each photo in this order too: put Caption or Date Taken first to hear it before the file name."

    init(model: LibraryModel) {
        self.model = model
        _editor = State(initialValue: ColumnsEditor(shown: model.columnOrder))
    }

    private var sample: FileRow? {
        model.focusedRow.flatMap { $0.isContainer ? nil : $0 } ?? model.rows.first { !$0.isContainer }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(Self.explanation)
                .fixedSize(horizontal: false, vertical: true)
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Available columns:")
                    List(selection: $editor.availableSelection) {
                        if editor.notShown.isEmpty {
                            // An empty list reads as nothing at all to VoiceOver.
                            Text("Every column is shown.").foregroundStyle(.secondary)
                        }
                        ForEach(editor.notShown, id: \.self) { id in
                            Text(ColumnOrder.title(id)).tag(id as String?)
                        }
                    }
                    .focused($focus, equals: .available)
                    .accessibilityLabel("Available columns")
                    .accessibilityHint(Self.explanation)
                    .onKeyPress(.return) { say(editor.add()); return .handled }
                }
                VStack(alignment: .leading, spacing: 4) {
                    Text("Shown columns, in the order they're read:")
                    List(selection: $editor.shownSelection) {
                        ForEach(editor.shown, id: \.self) { id in
                            Text(ColumnOrder.title(id)).tag(id as String?)
                        }
                    }
                    .focused($focus, equals: .shown)
                    .accessibilityLabel("Shown columns, in the order they're read")
                    .accessibilityHint(Self.explanation)
                    .onKeyPress(keys: [.delete, .deleteForward]) { _ in say(editor.remove()); return .handled }
                    .onKeyPress(keys: [.upArrow, .downArrow, .home, .end], phases: .down) { press in
                        guard press.modifiers == .option else { return .ignored }
                        switch press.key {
                        case .upArrow: say(editor.move(by: -1))
                        case .downArrow: say(editor.move(by: 1))
                        case .home: say(editor.moveToEnd(top: true))
                        default: say(editor.moveToEnd(top: false))
                        }
                        return .handled
                    }
                }
            }
            .frame(minHeight: 220)
            // Always enabled, so the order you meet them in never changes; each says when there's nothing it can do.
            HStack {
                Button("Add") { say(editor.add()) }
                Button("Remove") { say(editor.remove()) }
                Button("Move Up") { say(editor.move(by: -1)) }
                Button("Move Down") { say(editor.move(by: 1)) }
                Button("Move to Top") { say(editor.moveToEnd(top: true)) }
                Button("Move to Bottom") { say(editor.moveToEnd(top: false)) }
                Button("Reset to Default") { say(editor.reset()) }
            }
            VStack(alignment: .leading, spacing: 4) {
                Text("Preview of the selected photo's line:")
                Text(sample?.line(for: editor.shown) ?? "There's no photo here to preview.")
                    .textSelection(.enabled)
                    .accessibilityLabel("Preview of the selected photo's line")
                    .accessibilityValue(sample?.line(for: editor.shown) ?? "There's no photo here to preview.")
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("OK") {
                    if editor.shown != model.columnOrder { model.setColumnOrder(editor.shown) }
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding()
        .frame(minWidth: 620, minHeight: 480)
        .onAppear { focus = .shown }
    }

    private func say(_ text: String) {
        AccessibilityNotification.Announcement(text).post()
    }
}

/// The Columns sheet's state and actions, apart from the view so they can be tested. Each action returns what to
/// announce.
struct ColumnsEditor {
    private(set) var shown: [String]
    var shownSelection: String?
    var availableSelection: String?

    init(shown: [String]) {
        self.shown = ColumnOrder.clean(shown)
        shownSelection = self.shown.first
        availableSelection = notShown.first
    }

    var notShown: [String] { ColumnOrder.all.map(\.id).filter { !shown.contains($0) } }

    private func position(_ index: Int) -> String { "\(index + 1) of \(shown.count)" }

    /// Adds the selected available column at the end, and selects the next available one to add more.
    mutating func add() -> String {
        let rest = notShown
        guard !rest.isEmpty else { return "Every column is shown already." }
        guard let id = availableSelection, let index = rest.firstIndex(of: id) else { return "No column selected." }
        shown.append(id)
        shownSelection = id
        let remaining = notShown
        availableSelection = remaining.isEmpty ? nil : remaining[min(index, remaining.count - 1)]
        return "\(ColumnOrder.title(id)) added, \(position(shown.count - 1))."
    }

    mutating func remove() -> String {
        guard let id = shownSelection, let index = shown.firstIndex(of: id) else { return "No column selected." }
        guard id != ColumnOrder.name else { return "Name is always shown. It can be moved." }
        shown.remove(at: index)
        shownSelection = shown[min(index, shown.count - 1)]
        if availableSelection == nil { availableSelection = id }
        return "\(ColumnOrder.title(id)) removed. \(shown.count == 1 ? "1 column" : "\(shown.count) columns") shown."
    }

    mutating func move(by step: Int) -> String {
        guard let id = shownSelection, let index = shown.firstIndex(of: id) else { return "No column selected." }
        return moveTo(index, index + step, how: step < 0 ? "up" : "down", end: step < 0 ? "top" : "bottom")
    }

    mutating func moveToEnd(top: Bool) -> String {
        guard let id = shownSelection, let index = shown.firstIndex(of: id) else { return "No column selected." }
        let end = top ? "top" : "bottom"
        return moveTo(index, top ? 0 : shown.count - 1, how: "to \(end)", end: end)
    }

    private mutating func moveTo(_ index: Int, _ target: Int, how: String, end: String) -> String {
        let id = shown[index]
        guard index != target, shown.indices.contains(target) else { return "\(ColumnOrder.title(id)) is already at the \(end)." }
        shown.remove(at: index)
        shown.insert(id, at: target)
        shownSelection = id
        return "\(ColumnOrder.title(id)) moved \(how), \(position(target))."
    }

    mutating func reset() -> String {
        shown = ColumnOrder.defaultOrder
        shownSelection = shown.first
        availableSelection = notShown.first
        return "Columns reset to the default: \(shown.count) shown, Name first."
    }
}
