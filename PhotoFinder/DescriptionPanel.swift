import SwiftUI

/// View > Show Description (#50): the photo's description beside the list or gallery, for everyone who reads the
/// screen, with what's happening ("Describing…"), the caption, and the actions that go with them. VoiceOver users hear
/// descriptions as they move; here they can also read one by line, word or character, and correct it.
struct DescriptionPanel: View {
    @Environment(LibraryModel.self) private var model
    var pane: FocusState<Pane?>.Binding
    /// What's in the text box; saved when you press Return or leave the box, if you changed it.
    @State private var draft = ""
    /// The description the box was filled with, so only what you typed counts as a change.
    @State private var loadedText = ""
    @State private var draftID: String?

    private var isDirty: Bool { DescriptionDraft.isChanged(draft, from: loadedText) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let row = model.focusedRow, !row.isContainer, model.isDescribable(row.id) {
                // The photo itself, in list view, as Finder's and File Explorer's preview panes show it (Kelly, 13 Oct).
                // The gallery already shows it large.
                if model.viewMode == .list { PanelPreview(id: row.id) }
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
                // In a row when the panel is wide enough for their whole names, otherwise one under another: at the
                // panel's usual width a row cut them to "Describ…" and "Use as…" (#35).
                ViewThatFits(in: .horizontal) {
                    HStack { actions(for: row.id) }
                    VStack(alignment: .leading) { actions(for: row.id) }
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
                // What the file's own alt text says, read-only: Use as Caption replaces it, after asking.
                if !row.altText.isEmpty {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Alt text in the file")
                            .font(.subheadline.bold())
                        Text(row.altText)
                            .textSelection(.enabled)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .accessibilityElement(children: .combine)
                }
                Spacer(minLength: 0)
            } else {
                // Focusable, so F6 can land here and move on rather than getting stuck on a panel with nothing in it.
                Text(model.focusedRow == nil ? "Select a photo to see its description." : "Videos and folders aren't described.")
                    .foregroundStyle(.secondary)
                    .focusable()
                    .focused(pane, equals: .description)
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
        // A description arriving (automatic describing, Describe Again) shows at once, even while you're in the box,
        // unless you've started changing it.
        .onChange(of: model.descriptionRevision) {
            if !isDirty { load() }
        }
        .onChange(of: pane.wrappedValue) { old, new in
            if old == .description, new != .description, let draftID { save(draftID) }
        }
        // Hidden with Option-Command-I, or the window closed, while you were typing: keep what you typed.
        .onDisappear {
            if let draftID { save(draftID) }
        }
    }

    @ViewBuilder private func actions(for id: String) -> some View {
        Button("Describe Again") { model.describeFocusedPhotoAgain() }
            .fixedSize()
        Button("Use as Caption") { model.useDescriptionAsCaption([id]) }
            .disabled(model.savedDescription(for: id) == nil)
            .fixedSize()
        Button("Copy") { model.copyDescription(for: id) }
            .disabled(model.savedDescription(for: id) == nil)
            .fixedSize()
    }

    private func load() {
        draftID = model.focusedRow?.id
        loadedText = draftID.flatMap(model.savedDescription(for:)) ?? ""
        draft = loadedText
    }

    /// Saves the box's text for `id` if you changed it. An emptied box keeps the saved description.
    private func save(_ id: String) {
        guard isDirty else { return }
        if draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            model.saveEditedDescription(draft, for: id)  // says the saved one is kept
            if id == draftID { draft = loadedText }
            return
        }
        model.saveEditedDescription(draft, for: id)
        if id == draftID { loadedText = draft.trimmingCharacters(in: .whitespacesAndNewlines) }
    }
}

/// Whether the Description panel's box holds a change of the person's, compared with what it was filled with.
enum DescriptionDraft {
    static func isChanged(_ draft: String, from loaded: String) -> Bool {
        draft.trimmingCharacters(in: .whitespacesAndNewlines) != loaded.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

/// The photo you're on, above its description in list view, for anyone looking at the screen with you. Hidden from
/// VoiceOver: the description below says what's in it, and the photo's name is the heading under it.
private struct PanelPreview: View {
    let id: String
    @Environment(LibraryModel.self) private var model
    @State private var image: NSImage?
    @State private var loadedID: String?

    var body: some View {
        Group {
            if let image, loadedID == id {
                Image(nsImage: image)
                    .resizable()
                    .scaledToFit()
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            } else {
                RoundedRectangle(cornerRadius: 6)
                    .fill(.quaternary)
                    .overlay { ProgressView().controlSize(.small) }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: 240)
        .frame(height: 240)
        .accessibilityHidden(true)
        .task(id: id) {
            let loaded = await model.previewImage(for: id)
            guard !Task.isCancelled else { return }
            image = loaded
            loadedID = id
        }
    }
}
