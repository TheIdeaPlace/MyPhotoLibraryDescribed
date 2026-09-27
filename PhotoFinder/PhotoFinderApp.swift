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
                .task { await model.start() }
        }
        .commands { PhotoCommands(model: model) }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

struct PhotoCommands: Commands {
    let model: LibraryModel

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
            Menu("Columns") {
                ForEach(LibraryModel.optionalColumns) { column in
                    Toggle(column.title, isOn: Binding(
                        get: { model.isColumnVisible(column) },
                        set: { model.setColumn(column, visible: $0) }
                    ))
                }
            }
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
        }
        CommandMenu("Photo") {
            Button("Toggle Favorite") { model.toggleFavorite() }
                .keyboardShortcut("f", modifiers: [.command, .shift])
            Button("Edit Caption…") { model.editCaption() }
                .keyboardShortcut("e", modifiers: [.command, .shift])
        }
    }
}
