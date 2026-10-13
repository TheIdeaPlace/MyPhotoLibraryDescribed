import SwiftUI

/// My Photo Library Described > Settings (Command-,): the prompt Apple Intelligence describes photos with.
struct SettingsView: View {
    @AppStorage(DescriptionPrompt.key) private var prompt = ""
    @Environment(LibraryModel.self) private var model

    var body: some View {
        Form {
            Section {
                // A vertical text field rather than a text editor, so Tab still moves on and Return finishes.
                // Empty means the standard prompt, shown as the placeholder, so improvements to it reach everyone
                // who hasn't written their own.
                // The name above the field and the text from the left: a grouped form otherwise puts a field's text
                // on the right, below an empty space as tall as the field. The field carries the name for VoiceOver.
                VStack(alignment: .leading, spacing: 6) {
                    Text("Description prompt")
                        .accessibilityHidden(true)
                    TextField("Description prompt", text: $prompt, prompt: Text(DescriptionPrompt.standard), axis: .vertical)
                        .labelsHidden()
                        .multilineTextAlignment(.leading)
                        .lineLimit(4...10)
                        .textFieldStyle(.roundedBorder)
                        .accessibilityLabel("Description prompt")
                        .accessibilityHint("What Apple Intelligence is asked when it describes a photo. Leave it empty for the standard prompt.")
                }
                HStack {
                    Spacer()
                    Button("Use Standard Prompt") { prompt = "" }
                        .disabled(prompt.isEmpty)
                }
            } header: {
                Text("Describing Photos")
                    .accessibilityAddTraits(.isHeader)
            } footer: {
                Text("What Apple Intelligence is asked when it describes a photo with Describe Photo, Get Info or in the background. Left empty, the standard prompt (shown in the field) is used. Descriptions already saved are kept; Photo > Redescribe replaces one.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Section {
                // The same switch as the Photo menu's, asking the same question before it turns on.
                Toggle(model.saveInPhotosTitle, isOn: Binding(
                    get: { model.saveInPhotosShownOn },
                    set: { model.chooseSaveInPhotos($0) }
                ))
                // Made again when a question is cancelled, so a checkbox already showing on goes back to off.
                .id(model.switchesRedraw)
            } footer: {
                Text("Off until you turn it on. Then each new description is also saved in the photo, as its caption when it has none (in Photos, which syncs with iCloud Photos, or in the file itself for added folders), and in a file's alt text when it has none. Nothing you or other apps wrote is replaced. Photo > Use Description as Caption does it for the photos you choose, and asks before replacing anything.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Section {
                // Asks before it turns on, since it sends coordinates to Apple (#105).
                Toggle("Look Up Place Names", isOn: Binding(
                    get: { model.lookUpPlaceNames },
                    set: { model.setLookUpPlaceNames($0) }
                ))
                .id(model.switchesRedraw)
                .accessibilityHint("Sends a photo's coordinates to Apple to name the place it was taken, in Get Info.")
            } footer: {
                Text("Off until you turn it on. Then Get Info names the place a photo was taken, by sending its coordinates (nothing else) to Apple's location service. Coordinates are shown either way. Help > Privacy Policy says more.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        // A height of its own: a grouped form will take any height it's given, so without one the window could open
        // too short to show the last sections (#35). This shows every section; the form scrolls when a longer prompt
        // or larger text needs more.
        .frame(width: 520, height: 590)
        .padding()
    }
}
