import AppKit
import SwiftUI

/// Help > Check System…: the checks as a list, read one line at a time, with Copy to Clipboard and Report a Bug.
struct SystemCheckView: View {
    @Environment(LibraryModel.self) private var model
    @State private var check: SystemCheck?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let check {
                Text(check.summary)
                    .font(.headline)
                List(check.items) { item in
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Image(systemName: symbol(item.status))
                            .foregroundStyle(color(item.status))
                            .accessibilityHidden(true)
                        Text(item.line)
                            .textSelection(.enabled)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .accessibilityElement(children: .combine)
                }
                .accessibilityLabel("Checks")
                HStack {
                    Button("Check Again") { refresh() }
                    Spacer()
                    Button("Copy to Clipboard") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(check.text, forType: .string)
                        AccessibilityNotification.Announcement("Copied").post()
                    }
                    Button("Report a Bug…") { model.reportBug() }
                }
            } else {
                ProgressView("Checking…")
            }
        }
        .padding()
        .frame(minWidth: 520, minHeight: 360)
        .onAppear(perform: refresh)
        // Chosen again from the Help menu while open: check again rather than show old results.
        .onChange(of: model.systemCheckRequest) { refresh() }
    }

    private func refresh() {
        check = model.systemCheck()
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
