import SwiftUI

/// Photo Finder > Settings (Command-,): the prompt Apple Intelligence describes photos with.
struct SettingsView: View {
    @AppStorage(DescriptionPrompt.key) private var prompt = DescriptionPrompt.standard

    var body: some View {
        Form {
            Section {
                // A vertical text field rather than a text editor, so Tab still moves on and Return finishes.
                TextField("Description prompt", text: $prompt, axis: .vertical)
                    .lineLimit(4...10)
                    .textFieldStyle(.roundedBorder)
                HStack {
                    Spacer()
                    Button("Use Standard Prompt") { prompt = DescriptionPrompt.standard }
                        .disabled(prompt == DescriptionPrompt.standard)
                }
            } header: {
                Text("Describing Photos")
                    .accessibilityAddTraits(.isHeader)
            } footer: {
                Text("What Apple Intelligence is asked when it describes a photo with Describe Photo, Get Info or in the background. Left empty, the standard prompt is used. Descriptions already saved are kept; Describe Again in Get Info replaces one.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 520)
        .padding()
    }
}
