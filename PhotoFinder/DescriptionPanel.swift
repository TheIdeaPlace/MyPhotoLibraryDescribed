import SwiftUI

/// View > Show Description (#50): the photo's description beside the list or gallery, for everyone who reads the
/// screen, with what's happening ("Describing…"), the caption, and the actions that go with them. VoiceOver users hear
/// descriptions as they move; here they can also read one by line, word or character, and correct it.
struct DescriptionPanel: View {
    @Environment(LibraryModel.self) private var model
    var pane: FocusState<Pane?>.Binding
    /// What's in the text box; saved when you press Return or leave the box.
    @State private var draft = ""
    @State private var draftID: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let row = model.focusedRow, !row.isContainer, model.isDescribable(row.id) {
                Text(row.name)
                    .font(.headline)
                    .lineLimit(2)
                    .accessibilityAddTraits(.isHeader)
                Text(model.descriptionStatus(for: row.id))
                    .foregroundStyle(.secondary)
                // A vertical text field, not a text editor, so Tab still moves on and Return saves; Option-Return
                // starts a new line.
                TextField("Description", text: $draft, prompt: Text("No description yet"), axis: .vertical)
                    .lineLimit(6...30)
                    .textFieldStyle(.roundedBorder)
                    .focused(pane, equals: .description)
                    .onSubmit { save(row.id) }
                    .accessibilityHint("Edit to correct the description. Return saves it.")
                HStack {
                    Button("Describe Again") { model.describeFocusedPhotoAgain() }
                    Button("Use as Caption") { model.useDescriptionAsCaption([row.id]) }
                        .disabled(model.savedDescription(for: row.id) == nil)
                    Button("Copy") { model.copyDescription(for: row.id) }
                        .disabled(model.savedDescription(for: row.id) == nil)
                }
                .controlSize(.small)
                Divider()
                VStack(alignment: .leading, spacing: 4) {
                    Text("Caption")
                        .font(.subheadline.bold())
                    Text(row.caption.isEmpty ? "No caption" : row.caption)
                        .foregroundStyle(row.caption.isEmpty ? .secondary : .primary)
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .accessibilityElement(children: .combine)
                Button("Edit Caption…") { model.editCaption([row.id]) }
                    .controlSize(.small)
                Spacer(minLength: 0)
            } else {
                Text(model.focusedRow == nil ? "Select a photo to see its description." : "Videos and folders aren't described.")
                    .foregroundStyle(.secondary)
                Spacer(minLength: 0)
            }
        }
        .padding()
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Description")
        .onAppear(perform: load)
        .onChange(of: model.focusedRow?.id) { oldID, _ in
            // Moving to another photo keeps an unsaved correction rather than dropping it.
            if let oldID, oldID == draftID { save(oldID) }
            load()
        }
        // A description arriving (automatic describing, Describe Again) shows at once, unless you're typing.
        .onChange(of: model.descriptionRevision) {
            if pane.wrappedValue != .description { load() }
        }
        .onChange(of: pane.wrappedValue) { old, new in
            if old == .description, new != .description, let draftID { save(draftID) }
        }
    }

    private func load() {
        draftID = model.focusedRow?.id
        draft = draftID.flatMap(model.savedDescription(for:)) ?? ""
    }

    private func save(_ id: String) {
        let saved = model.savedDescription(for: id) ?? ""
        guard draft.trimmingCharacters(in: .whitespacesAndNewlines) != saved else { return }
        // Clearing the box keeps the saved description (it can't be empty); show it again.
        if draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !saved.isEmpty {
            model.saveEditedDescription(draft, for: id)
            if id == draftID { draft = saved }
            return
        }
        model.saveEditedDescription(draft, for: id)
    }
}
