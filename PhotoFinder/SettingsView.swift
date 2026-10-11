import SwiftUI

/// Photo Finder > Settings (Command-,): the prompt Apple Intelligence describes photos with.
struct SettingsView: View {
    @AppStorage(DescriptionPrompt.key) private var prompt = ""

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
                Text("What Apple Intelligence is asked when it describes a photo with Describe Photo, Get Info or in the background. Left empty, the standard prompt (shown in the field) is used. Descriptions already saved are kept; Describe Again in Get Info replaces one.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 520)
        .padding()
    }
}
