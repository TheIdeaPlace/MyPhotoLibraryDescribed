import SwiftUI

@main
struct PhotoFinderApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var model = LibraryModel()

    var body: some Scene {
        Window(AppInfo.name, id: "main") {
            ContentView()
                .environment(model)
                .frame(minWidth: 720, minHeight: 420)
                .task {
                    // Unit tests run inside the app; they don't need (and can't answer) the Photos permission prompt.
                    guard ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil else { return }
                    await model.start()
                    model.checkForUpdatesQuietly()
                    model.sayIfJustUpdated()
                }
        }
        .commands { PhotoCommands(model: model) }

        Settings {
            SettingsView()
                .environment(model)
        }

        Window("Check System", id: "systemCheck") {
            SystemCheckView()
                .environment(model)
        }
        .windowResizability(.contentSize)

        Window("Check AI Description", id: "aiDescriptionCheck") {
            AIDescriptionCheckView()
                .environment(model)
        }
        .windowResizability(.contentSize)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

struct PhotoCommands: Commands {
    let model: LibraryModel
    @Environment(\.openWindow) private var openWindow
    private static let f6 = KeyEquivalent(Character(UnicodeScalar(NSF6FunctionKey)!))
    private static let f9 = KeyEquivalent(Character(UnicodeScalar(NSF9FunctionKey)!))

    var body: some Commands {
        CommandGroup(after: .newItem) {
            // Read-only (#84): checked at every launch; unchecking asks first.
            Toggle("Read-Only", isOn: Binding(
                get: { model.readOnly },
                set: { model.setReadOnly($0) }
            ))
            Divider()
            // As on Windows (#36). Remove is always enabled and says when you're not in an added folder.
            Button("Add Folder to Library…") { model.addFolder() }
            Button("Remove Folder from Library") { model.removeCurrentFolder() }
            Divider()
            Button("Open") { model.open() }
                .keyboardShortcut("o")
            Button("Quick Look") { model.quickLook() }
                .keyboardShortcut("y")
            Button("Get Info") { model.showInfo() }
                .keyboardShortcut("i")
            Divider()
            Button("Export…") { model.exportSelection() }
                .keyboardShortcut("e")
        }
        CommandGroup(after: .textEditing) {
            Button("Find") { model.searchFocusRequest += 1 }
                .keyboardShortcut("f")
        }
        CommandGroup(before: .toolbar) {
            Toggle("as List", isOn: Binding(
                get: { model.viewMode == .list },
                set: { _ in model.viewMode = .list }
            ))
            .keyboardShortcut("2")
            Toggle("as Gallery", isOn: Binding(
                get: { model.viewMode == .gallery },
                set: { _ in model.viewMode = .gallery }
            ))
            .keyboardShortcut("4")
            Button("Columns…") { model.columnsSheetShown = true }
            // The Description panel (#50); Option-Command-I, as Finder's Show Inspector.
            Toggle("Show Description", isOn: Binding(
                get: { model.showDescriptionPanel },
                set: { model.showDescriptionPanel = $0 }
            ))
            .keyboardShortcut("i", modifiers: [.command, .option])
            // As on Windows: sorting without a column header. The checked column is the current sort.
            Menu("Sort By") {
                ForEach(ColumnOrder.all) { column in
                    Toggle(column.title, isOn: Binding(
                        get: { model.sortColumnID == column.id },
                        set: { _ in model.sortBy(column.id) }
                    ))
                }
            }
            // Windows' Refresh (F5): reads added folders again, and the place showing.
            Button("Refresh") { model.refresh() }
                .keyboardShortcut("r")
            Divider()
        }
        // Help: the same items as on Windows (#32, #33), plus macOS's own Help search. About stays in the app menu,
        // where Mac apps keep it. Check for Updates comes with a signed release (#22).
        CommandGroup(replacing: .help) {
            // The order agreed with Windows in #13: Check for Updates…, Report a Bug…, then Check System…. The first
            // names the new version once one is found, as the app kit asks.
            Button(Updates.menuTitle(running: AppInfo.shortVersion, available: model.availableUpdate?.version)) {
                model.checkForUpdates()
            }
            Button("Report a Bug…") { model.reportBug() }
            Button("Check System…") {
                model.systemCheckRequest += 1
                openWindow(id: "systemCheck")
            }
            // After Check System, as on Windows (#95).
            Button("Check AI Description…") {
                model.aiDescriptionCheckRequest += 1
                openWindow(id: "aiDescriptionCheck")
            }
            // The diagnostic log (#103), as on Windows.
            Button("Open Log Folder") { model.openLogFolder() }
            Divider()
            Button("\(AppInfo.name) User Guide") { NSWorkspace.shared.open(AppInfo.userGuideURL) }
            Button("Privacy Policy") { NSWorkspace.shared.open(AppInfo.privacyPolicyURL) }
        }
        CommandMenu("Go") {
            Button("Enclosing Folder") { model.goToEnclosingFolder() }
                .keyboardShortcut(.upArrow, modifiers: .command)
            Button("Open Selected Item") { model.open() }
                .keyboardShortcut(.downArrow, modifiers: .command)
            Divider()
            Button("All Photos") { model.navigate(to: SidebarID.allPhotos) }
                .keyboardShortcut("a", modifiers: [.command, .shift])
            Divider()
            // F6 and Shift-F6, as on Windows: sidebar, list or gallery, Search, status bar.
            Button("Next Pane") { model.movePane(by: 1) }
                .keyboardShortcut(Self.f6, modifiers: [])
            Button("Previous Pane") { model.movePane(by: -1) }
                .keyboardShortcut(Self.f6, modifiers: .shift)
            // The status bar from anywhere (#84): Control-F9 reads it, Control-Shift-F9 goes to it. In Go, with the
            // panes, rather than View (Kelly, 13 Oct).
            Button("Go to Status Bar") { model.goToStatusBar() }
                .keyboardShortcut(Self.f9, modifiers: [.control, .shift])
            Button("Read Status Bar") { model.readStatusBar() }
                .keyboardShortcut(Self.f9, modifiers: .control)
        }
        CommandMenu("Photo") {
            Button("Toggle Favorite") { model.toggleFavorite() }
                .keyboardShortcut("f", modifiers: [.command, .shift])
            // As on Windows: No Rating, then 1 to 5 stars (#20).
            Menu("Rating") {
                ForEach(0...5, id: \.self) { stars in
                    Button(RatingText.menuTitle(stars)) { model.setRating(stars) }
                }
            }
            Button("Edit Caption…") { model.editCaption() }
                .keyboardShortcut("e", modifiers: [.command, .shift])
            Button("Use Description as Caption") { model.useDescriptionAsCaption() }
            // Paused, and shown unchecked, while read-only (as on Windows).
            Toggle(model.saveInPhotosTitle, isOn: Binding(
                get: { model.saveInPhotosShownOn },
                set: { model.chooseSaveInPhotos($0) }
            ))
            Divider()
            Button("Describe Photo") { model.describeFocusedPhoto() }
                .keyboardShortcut("d", modifiers: [.command, .shift])
            Button("Describe Again") { model.describeFocusedPhotoAgain() }
                .keyboardShortcut("d", modifiers: [.command, .shift, .option])
            Toggle("Describe Photos Automatically", isOn: Binding(
                get: { model.describeAutomatically },
                set: { model.setDescribeAutomatically($0) }
            ))
            Toggle("Describe Photos in the Background", isOn: Binding(
                get: { model.describeInBackground },
                set: { model.setDescribeInBackground($0) }
            ))
            // Always enabled (it says when nothing is running), like the Columns sheet's buttons.
            Button("Stop Background Describing") { model.stopBackgroundDescribing() }
        }
    }
}
