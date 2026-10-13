import AppKit
import SwiftUI

/// Help > Check System…: the checks as a list, read one line at a time, with Copy to Clipboard and Report a Bug.
struct SystemCheckView: View {
    @Environment(LibraryModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openWindow) private var openWindow
    @State private var check: SystemCheck?
    /// The check you're on, so the arrow keys move through them and VoiceOver reads each one.
    @State private var selected: SystemCheck.Item.ID?
    @FocusState private var listFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let check {
                Text(check.summary)
                    .font(.headline)
                // A list you can arrow through, one check a row: with a selection, so the arrow keys move and
                // VoiceOver reads the row's text (a grouped row read only as "cell"). The OK or Needs attention is in
                // the words, so the symbol is only for the eye.
                List(check.items, selection: $selected) { item in
                    Label {
                        Text(item.line)
                            .fixedSize(horizontal: false, vertical: true)
                    } icon: {
                        Image(systemName: symbol(item.status))
                            .foregroundStyle(color(item.status))
                            .accessibilityHidden(true)
                    }
                    .accessibilityLabel(item.line)
                    .tag(item.id)
                }
                .focused($listFocused)
                .defaultFocus($listFocused, true)
                .accessibilityLabel("Checks")
                HStack {
                    Button("Check Again") { refresh() }
                    Spacer()
                    Button("Copy to Clipboard") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(check.text, forType: .string)
                        AccessibilityNotification.Announcement("Copied").post()
                    }
                    Button("Report a Bug…") {
                        model.prepareBugReport()
                        openWindow(id: BugReportView.windowID)
                    }
                }
            } else {
                ProgressView("Checking…")
            }
        }
        .padding()
        .frame(minWidth: 520, minHeight: 360)
        .closesWithKeyboard { dismiss() }
        .onAppear(perform: refresh)
        // The library's photos are counted in the background; the line fills in when they are.
        .onChange(of: model.libraryCoverage?.described) { check = model.systemCheck() }
        .onChange(of: model.libraryCoverage?.total) { check = model.systemCheck() }
        // Chosen again from the Help menu while open: check again rather than show old results.
        .onChange(of: model.systemCheckRequest) { refresh() }
    }

    private func refresh() {
        let first = check == nil
        model.countLibraryCoverage()
        check = model.systemCheck()
        // On the first check, ready to arrow through, and VoiceOver starts there. Checking again keeps your place.
        if selected == nil || !(check?.items.contains { $0.id == selected } ?? false) {
            selected = check?.items.first?.id
        }
        // Set a moment later the first time: the list has only just appeared, and focus set in the same update is
        // often lost.
        if first { Task { @MainActor in listFocused = true } }
        if let check { AccessibilityNotification.Announcement(check.summary).post() }
    }

    private func symbol(_ status: SystemCheck.Item.Status) -> String {
        switch status {
        case .ok: "checkmark.circle.fill"
        case .problem: "exclamationmark.triangle.fill"
        case .info: "info.circle"
        }
    }

    private func color(_ status: SystemCheck.Item.Status) -> Color {
        switch status {
        case .ok: .green
        case .problem: .orange
        case .info: .secondary
        }
    }
}
