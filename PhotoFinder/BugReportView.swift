import AppKit
import SwiftUI

/// Help > Report a Bug… (#33): the app kit's form. A bug or a suggestion, a title, the questions, an optional contact,
/// whether to include the system check, and a preview of exactly what Send sends. Focus starts in Title. Escape and
/// Command-W close it. Once filed, it says the issue's number, with a button to open it.
struct BugReportView: View {
    static let windowID = "reportBug"
    static let contactNote = "Optional, for example an email address. Everything in a report is public on GitHub, including this."

    @Environment(LibraryModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var report = BugReport()
    @State private var sending = false
    @State private var filedNumber: Int?
    @State private var filedURL: URL?
    @FocusState private var focus: Field?

    private enum Field: Hashable { case title, openIssue }

    var body: some View {
        Group {
            if let filedNumber, let filedURL {
                filed(filedNumber, filedURL)
            } else {
                form
            }
        }
        .padding()
        .frame(minWidth: 560)
        // While the form shows, its Cancel button answers Escape.
        .closesWithKeyboard(escape: filedNumber != nil) { dismiss() }
        .onAppear {
            // Opened some other way than Report a Bug… (it never should be): start from a fresh draft all the same.
            if model.bugReportRequest == 0 { model.prepareBugReport() }
            load()
        }
        // Report a Bug… chosen again, or from Check System, while the form is open: take the new details.
        .onChange(of: model.bugReportRequest) { load() }
    }

    private var form: some View {
        VStack(alignment: .leading, spacing: 12) {
            Form {
                Picker("Kind", selection: $report.kind) {
                    ForEach(BugReport.Kind.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.radioGroup)
                .horizontalRadioGroupLayout()
                TextField("Title", text: $report.title)
                    .focused($focus, equals: .title)
                switch report.kind {
                case .bug:
                    TextField("What happened", text: $report.happened, axis: .vertical)
                        .lineLimit(3...6)
                    TextField("What you expected", text: $report.expected, axis: .vertical)
                        .lineLimit(2...6)
                    TextField("Steps", text: $report.steps, axis: .vertical)
                        .lineLimit(3...6)
                case .suggestion:
                    TextField("Suggestion", text: $report.suggestion, axis: .vertical)
                        .lineLimit(4...8)
                }
                // The note is said with the field as well as shown, so nobody types an address without hearing it.
                TextField("Contact", text: $report.contact)
                    .accessibilityHint(Self.contactNote)
                Text(Self.contactNote)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
                Toggle("Include the system check", isOn: $report.includeSystemCheck)
                if !report.attachments.isEmpty {
                    Toggle(report.attachmentsToggleTitle, isOn: $report.includeAttachments)
                }
            }
            Text("Preview of what will be sent")
                .font(.headline)
                .accessibilityAddTraits(.isHeader)
            ScrollView {
                Text(report.preview)
                    .font(.body.monospaced())
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(6)
            }
            .frame(minHeight: 160, maxHeight: 260)
            .background(Color(nsColor: .textBackgroundColor))
            .accessibilityLabel("Preview")
            if AppInfo.relayKey == nil {
                Text("This copy of \(AppInfo.name) can't send reports itself, so Send opens a new GitHub issue in your browser, with the report on the clipboard.")
                    .font(.callout)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                if sending { ProgressView().controlSize(.small).accessibilityHidden(true) }
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                // Not the default button: Return in a field mustn't send a report half written.
                Button(sending ? "Sending…" : "Send") { send() }
                    .disabled(sending)
            }
        }
    }

    private func filed(_ number: Int, _ url: URL) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Report filed as issue #\(number)")
                .font(.headline)
            Text("Thank you. It's public on GitHub, where you can follow it.")
            HStack {
                Spacer()
                Button("Open Issue") { NSWorkspace.shared.open(url) }
                    .focused($focus, equals: .openIssue)
                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
        }
    }

    /// The model's draft, keeping anything already typed: only the system check and what came with it are replaced.
    private func load() {
        let draft = model.bugReportDraft
        if report.isUntouched {
            report = draft
        } else {
            report.systemText = draft.systemText
            report.attachments = draft.attachments
        }
        filedNumber = nil
        filedURL = nil
        // A moment later: the form has only just appeared, and focus set in the same update is often lost.
        Task { @MainActor in focus = .title }
    }

    private func send() {
        sending = true
        announce("Sending")
        let report = report
        Task { @MainActor in
            let result = await model.sendBugReport(report)
            sending = false
            switch result {
            case .filed(let number, let url):
                self.report = BugReport()
                filedNumber = number
                filedURL = url
                announce("Report filed as issue #\(number)")
                Task { @MainActor in focus = .openIssue }
            case .noKey:
                announce("Opened in your browser to submit. The report is also on the clipboard.")
                dismiss()
            case .failed:
                announce("Couldn't send it, so it's open in your browser to submit. The report is also on the clipboard.")
                dismiss()
            }
        }
    }

    private func announce(_ message: String) {
        AccessibilityNotification.Announcement(message).post()
    }
}

extension BugReport {
    /// Nothing typed yet, so a new draft can replace it whole.
    var isUntouched: Bool {
        [title, happened, expected, steps, suggestion, contact].allSatisfy {
            $0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }
    }
}

/// The main window opens the form when the model asks: the crash offer at launch isn't in a view, so can't itself.
struct OpensBugReportWindow: ViewModifier {
    @Environment(LibraryModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    func body(content: Content) -> some View {
        content.onChange(of: model.bugReportWindowRequest) { openWindow(id: BugReportView.windowID) }
    }
}
