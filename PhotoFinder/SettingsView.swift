import SwiftUI

/// Photo Finder > Settings (Command-,): the prompt Apple Intelligence describes photos with.
struct SettingsView: View {
    @AppStorage(DescriptionPrompt.key) private var prompt = ""
    @Environment(LibraryModel.self) private var model

    var body: some View {
        Form {
            Section {
                // A vertical text field rather than a text editor, so Tab still moves on and Return finishes.
                // Empty means the standard prompt, shown as the placeholder, so improvements to it reach everyone
                // who hasn't written their own.
                TextField("Description prompt", text: $prompt, prompt: Text(DescriptionPrompt.standard), axis: .vertical)
                    .lineLimit(4...10)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityHint("What Apple Intelligence is asked when it describes a photo. Leave it empty for the standard prompt.")
                HStack {
                    Spacer()
                    Button("Use Standard Prompt") { prompt = "" }
                        .disabled(prompt.isEmpty)
                }
            } header: {
                Text("Describing Photos")
                    .accessibilityAddTraits(.isHeader)
            } footer: {
                Text("What Apple Intelligence is asked when it describes a photo with Describe Photo, Get Info or in the background. Left empty, the standard prompt (shown in the field) is used. Descriptions already saved are kept; Photo > Describe Again replaces one.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Section {
                // The same switch as the Photo menu's, asking the same question before it turns on.
                Toggle(model.saveInPhotosTitle, isOn: Binding(
                    get: { model.saveInPhotosShownOn },
                    set: { model.chooseSaveInPhotos($0) }
                ))
            } footer: {
                Text("Off until you turn it on. Then each new description is also saved in the photo, as its caption when it has none (in Photos, which syncs with iCloud Photos, or in the file itself for added folders), and in a file's alt text when it has none. Nothing you or other apps wrote is replaced. Photo > Use Description as Caption does it for the photos you choose, and asks before replacing anything.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 520)
        .padding()
    }
}
