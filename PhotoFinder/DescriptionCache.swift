import Foundation
import Photos

/// Apple Intelligence descriptions already made, saved to disk so a photo is described once: by Describe Image in
/// Get Info, or behind the scenes when it's added to the library. Keyed by the photo's local identifier, so
/// favoriting or captioning it (which changes its modification date) keeps its description. Files in added folders
/// (#36) are keyed by path, size and modified time instead (see FileDescriptionKey), as on Windows.
final class DescriptionCache: @unchecked Sendable {
    /// Posted (on the main queue) when a description is saved or replaced, so what's on screen can show it.
    static let didChange = Notification.Name("DescriptionCacheDidChange")
    /// In didChange's userInfo: the photo's key and its new description.
    static let keyInfo = "key", textInfo = "text"

    private var entries: [String: String] = [:]
    /// Descriptions the person corrected in the Description panel (#50). In a file of their own next to
    /// Descriptions.json, so that file keeps the format Windows' store shares.
    private var edited: Set<String> = []
    private let lock = NSLock()
    private let fileURL: URL
    private var editedURL: URL { fileURL.deletingLastPathComponent().appendingPathComponent("EditedDescriptions.json") }
    private var saveScheduled = false

    init(fileURL: URL = DescriptionCache.defaultURL) {
        self.fileURL = fileURL
        if let data = try? Data(contentsOf: fileURL),
           let saved = try? JSONDecoder().decode([String: String].self, from: data) {
            entries = saved
        }
        if let data = try? Data(contentsOf: editedURL), let saved = try? JSONDecoder().decode([String].self, from: data) {
            edited = Set(saved)
        }
    }

    /// Whether the person corrected this description, so Describe Again asks before replacing it.
    func isEdited(forKey key: String) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return edited.contains(key)
    }

    static var defaultURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(AppInfo.dataFolderName, isDirectory: true)
            .appendingPathComponent("Descriptions.json")
    }

    func description(forKey key: String) -> String? {
        lock.lock()
        defer { lock.unlock() }
        return entries[key]
    }

    /// Saves a description. `edited` marks one the person wrote or corrected; a new description made by Apple
    /// Intelligence clears the mark.
    func remember(_ text: String, forKey key: String, edited isEdited: Bool = false) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        lock.lock()
        entries[key] = trimmed
        if isEdited { edited.insert(key) } else { edited.remove(key) }
        let schedule = !saveScheduled
        saveScheduled = true
        lock.unlock()
        // A couple of seconds after the last change, not after every one: describing in the background adds
        // descriptions one after another, and rewriting the whole file each time adds up.
        if schedule {
            DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 2) { [self] in save() }
        }
        DispatchQueue.main.async {
            NotificationCenter.default.post(name: Self.didChange, object: self, userInfo: [Self.keyInfo: key, Self.textInfo: trimmed])
        }
    }

    var count: Int {
        lock.lock()
        defer { lock.unlock() }
        return entries.count
    }

    func description(for asset: PHAsset) -> String? { description(forKey: asset.localIdentifier) }

    func remember(_ text: String, for asset: PHAsset) { remember(text, forKey: asset.localIdentifier) }

    func save() {
        lock.lock()
        saveScheduled = false
        let snapshot = entries
        let editedSnapshot = edited.sorted()
        lock.unlock()
        guard let data = try? JSONEncoder().encode(snapshot) else { return }
        try? FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
        if let editedData = try? JSONEncoder().encode(editedSnapshot) { try? editedData.write(to: editedURL, options: .atomic) }
    }
}
