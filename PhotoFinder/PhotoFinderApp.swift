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
                }
        }
        .commands { PhotoCommands(model: model) }

        Settings {
            SettingsView()
        }

        Window("Check System", id: "systemCheck") {
            SystemCheckView()
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

    var body: some Commands {
        CommandGroup(after: .newItem) {
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
            // As on Windows: sorting without a column header. The checked column is the current sort.
            Menu("Sort By") {
                ForEach(ColumnOrder.all) { column in
                    Toggle(column.title, isOn: Binding(
                        get: { model.sortColumnID == column.id },
                        set: { _ in model.sortBy(column.id) }
                    ))
                }
            }
            Divider()
        }
        // Help: the same items as on Windows (#32, #33), plus macOS's own Help search. About stays in the app menu,
        // where Mac apps keep it. Check for Updates comes with a signed release (#22).
        CommandGroup(replacing: .help) {
            Button("Check System…") {
                model.systemCheckRequest += 1
                openWindow(id: "systemCheck")
            }
            Button("Report a Bug…") { model.reportBug() }
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
        }
        CommandMenu("Photo") {
            Button("Toggle Favorite") { model.toggleFavorite() }
                .keyboardShortcut("f", modifiers: [.command, .shift])
            Button("Edit Caption…") { model.editCaption() }
                .keyboardShortcut("e", modifiers: [.command, .shift])
            Divider()
            Button("Describe Photo") { model.describeFocusedPhoto() }
                .keyboardShortcut("d", modifiers: [.command, .shift])
            Button("Describe Again") { model.describeFocusedPhotoAgain() }
                .keyboardShortcut("d", modifiers: [.command, .shift, .option])
            Toggle("Describe Photos Automatically", isOn: Binding(
                get: { model.describeAutomatically },
                set: { model.setDescribeAutomatically($0) }
            ))
            Toggle("Describe New Photos in the Background", isOn: Binding(
                get: { model.describeInBackground },
                set: { model.setDescribeInBackground($0) }
            ))
            // Always enabled (it says when nothing is running), like the Columns sheet's buttons.
            Button("Stop Background Describing") { model.stopBackgroundDescribing() }
        }
    }
}
