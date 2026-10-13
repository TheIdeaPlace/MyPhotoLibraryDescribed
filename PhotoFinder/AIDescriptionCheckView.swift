import AppKit
import SwiftUI

/// Help > Check AI Description… (#95): the sample photo's description first, so focus lands on it and VoiceOver reads
/// it, then whether it mentions what the sample shows, the time and the model, with Redescribe, Copy to Clipboard
/// and Report a Bug. Laid out like Check System, as on Windows, where both use the same window.
struct AIDescriptionCheckView: View {
    @Environment(LibraryModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var result: AIDescriptionCheck?
    @State private var running: Task<Void, Never>?
    @State private var selected: SystemCheck.Item.ID?
    @FocusState private var listFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let result {
                Text(result.summary)
                    .font(.headline)
                List(result.items, selection: $selected) { item in
                    Text(item.line)
                        .fixedSize(horizontal: false, vertical: true)
                        .textSelection(.enabled)
                        .accessibilityLabel(item.line)
                        .tag(item.id)
                }
                .focused($listFocused)
                .accessibilityLabel("Result")
                HStack {
                    Button("Redescribe") { run() }
                    Spacer()
                    Button("Copy to Clipboard") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(result.text, forType: .string)
                        AccessibilityNotification.Announcement("Copied").post()
                    }
                    Button("Report a Bug…") { model.reportBug(aiCheck: result) }
                }
            } else {
                ProgressView("Describing the sample photo…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .padding()
        .frame(minWidth: 520, minHeight: 360)
        .closesWithKeyboard { dismiss() }
        .onAppear(perform: run)
        // Chosen again from the Help menu while open: describe again rather than show the old result.
        .onChange(of: model.aiDescriptionCheckRequest) { run() }
        // Closing gives it up, and nothing is said for it afterwards.
        .onDisappear {
            running?.cancel()
            running = nil
        }
    }

    private func run() {
        guard running == nil else { return }
        // The list goes while it works, so the new one appears afresh and focus reads its first line, the description.
        result = nil
        selected = nil
        // Reset, so setting it again once the new list appears moves focus there.
        listFocused = false
        // Not when Apple Intelligence can't describe here: the result, at once, says why. Queued behind the window's
        // title, which VoiceOver is reading as it opens, rather than cutting it off.
        if model.describeUnavailableReason == nil {
            var starting = AttributedString("Describing the sample photo")
            starting.accessibilitySpeechAnnouncementPriority = .low
            AccessibilityNotification.Announcement(starting).post()
        }
        running = Task { @MainActor in
            let check = await model.runAIDescriptionCheck()
            // Given up (the window closed): the window's next run is its own.
            guard !Task.isCancelled else { return }
            running = nil
            guard let check else { return }
            result = check
            selected = check.items.first?.id
            // A moment later: the list has only just appeared, and focus set in the same update is often lost.
            Task { @MainActor in
                listFocused = true
                // After VoiceOver reads the description focus landed on, rather than cutting it off.
                var summary = AttributedString(check.summary)
                summary.accessibilitySpeechAnnouncementPriority = .low
                AccessibilityNotification.Announcement(summary).post()
            }
        }
    }
}
