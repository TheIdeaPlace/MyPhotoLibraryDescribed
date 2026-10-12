import SwiftUI

@main
struct PhotoFinderApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var model = LibraryModel()

    var body: some Scene {
        Window("Photo Finder", id: "main") {
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
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

struct PhotoCommands: Commands {
    let model: LibraryModel
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
            Divider()
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
