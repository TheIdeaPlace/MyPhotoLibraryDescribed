import Foundation
import FoundationModels
import Photos

/// One saved description and where it came from (#199): the model, the prompt, the system and app versions, and when,
/// so descriptions can be told apart later (if Apple Intelligence changes, say) and exported with their details. The
/// field names are shared with Windows' store. A field that isn't known (a description saved before #199) is left
/// out of the file.
struct DescriptionRecord: Codable, Equatable, Sendable {
    /// The description itself.
    var text: String
    /// Who made it: `apple-intelligence`, or `person` when it was written or corrected in the Description panel.
    var source: String?
    /// The prompt Apple Intelligence was asked with: the standard one or the person's own, as text.
    var prompt: String?
    /// The model, as far as it can be known: Apple Intelligence's system model variant ("AFM 3 Core").
    var model: String?
    /// The language the description was asked in, the Mac's locale ("en_US").
    var locale: String?
    /// macOS's version and build, "macOS 27.2 (26B5101f)", which also identifies the system model's version.
    var os: String?
    /// The app's version and build, "0.2.0 (12)".
    var app: String?
    /// When it was described, ISO 8601 in UTC: "2026-10-14T18:30:05Z".
    var created: String?
    /// About how long Apple Intelligence took to describe it, in milliseconds.
    var durationMs: Int?
    /// When the person corrected it, ISO 8601 in UTC; nil when it's as Apple Intelligence made it.
    var edited: String?
    /// Apple Intelligence's text before the person corrected it: the first, kept through later corrections.
    var original: String?

    /// Whether the person wrote or corrected it, so it isn't replaced without asking.
    var isEdited: Bool { source == DescriptionProvenance.person }
}

/// Where a new description came from, as the describe paths know it. The record's other details (the system and app
/// versions) are filled in here, so every path saves them the same way.
struct DescriptionProvenance: Equatable, Sendable {
    static let appleIntelligence = "apple-intelligence"
    static let person = "person"

    var source: String
    var prompt: String?
    var model: String?
    var locale: String?
    var os: String? = Self.currentOS
    var app: String? = Self.currentApp
    var created = Date()
    var durationMs: Int?

    /// A description Apple Intelligence made with `prompt`, taking `seconds`.
    static func appleIntelligence(prompt: String, seconds: Double? = nil, model: String? = AppleIntelligenceModel.name,
                                  locale: String = Locale.current.identifier) -> DescriptionProvenance {
        DescriptionProvenance(source: appleIntelligence, prompt: prompt, model: model, locale: locale,
                              durationMs: seconds.map { max(0, Int(($0 * 1000).rounded())) })
    }

    /// "macOS 27.2 (26B5101f)", read once: it doesn't change while the app runs.
    static let currentOS = SystemCheck.osText(ProcessInfo.processInfo.operatingSystemVersion, build: SystemDetails.osBuild)
    static let currentApp = AppInfo.version

    /// ISO 8601 in UTC, to the second: "2026-10-14T18:30:05Z".
    static func timestamp(_ date: Date) -> String { date.formatted(.iso8601) }

    func record(_ text: String) -> DescriptionRecord {
        DescriptionRecord(text: text, source: source, prompt: prompt, model: model, locale: locale, os: os, app: app,
                          created: Self.timestamp(created), durationMs: durationMs)
    }
}

/// Apple Intelligence's model as FoundationModels names it. macOS 27 gives the system model's variant ("AFM 3 Core");
/// there's no version number of its own, so the record's macOS build stands for it.
enum AppleIntelligenceModel {
    static let fallback = "apple-intelligence-system"

    static let name: String = {
#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older
        if #available(macOS 27, *) {
            let variant = SystemLanguageModel.default.variant.displayName
            if !variant.isEmpty { return variant }
        }
#endif
        return fallback
    }()
}

/// Apple Intelligence descriptions already made, saved to disk so a photo is described once: by Describe Image in
/// Get Info, or behind the scenes when it's added to the library. Keyed by the photo's local identifier, so
/// favoriting or captioning it (which changes its modification date) keeps its description. Files in added folders
/// (#36) are keyed by path, size and modified time instead (see FileDescriptionKey), as on Windows.
///
/// Descriptions.json is `{"version": 2, "descriptions": {key: record}}` (#199), each record a DescriptionRecord; the
/// person's corrections are marked in their records. A file from before #199 (`{key: text}`, with the corrected keys in
/// EditedDescriptions.json beside it) is read, its details unknown, and written in the new form at the next save.
final class DescriptionCache: @unchecked Sendable {
    /// Posted (on the main queue) when a description is saved or replaced, so what's on screen can show it.
    static let didChange = Notification.Name("DescriptionCacheDidChange")
    /// In didChange's userInfo: the photo's key and its new description.
    static let keyInfo = "key", textInfo = "text"
    /// The file's format, written at its top level.
    static let fileVersion = 2

    private var entries: [String: DescriptionRecord] = [:]
    private let lock = NSLock()
    private let fileURL: URL
    /// The folder the descriptions are saved in: the app's data folder, or a test's.
    var folderURL: URL { fileURL.deletingLastPathComponent() }
    private var saveScheduled = false

    private struct File: Codable {
        var version: Int
        var descriptions: [String: DescriptionRecord]
    }

    init(fileURL: URL = DescriptionCache.defaultURL) {
        self.fileURL = fileURL
        guard let data = try? Data(contentsOf: fileURL) else { return }
        if let file = try? JSONDecoder().decode(File.self, from: data) {
            entries = file.descriptions
        } else if let flat = try? JSONDecoder().decode([String: String].self, from: data) {
            // Before #199: text only, and the corrections listed in a file of their own.
            let editedURL = folderURL.appendingPathComponent("EditedDescriptions.json")
            let edited = (try? Data(contentsOf: editedURL)).flatMap { try? JSONDecoder().decode([String].self, from: $0) } ?? []
            entries = flat.mapValues { DescriptionRecord(text: $0) }
            for key in edited where entries[key] != nil { entries[key]?.source = DescriptionProvenance.person }
        }
    }

    /// Whether a new description from Apple Intelligence may replace the saved one: always, unless the person
    /// corrected it, in which case only the very correction they agreed Redescribe may replace.
    static func mayReplace(edited: Bool, saved: String?, agreed: String?) -> Bool {
        !edited || (agreed != nil && saved == agreed)
    }

    /// Whether the person corrected this description, so Redescribe asks before replacing it.
    func isEdited(forKey key: String) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return entries[key]?.isEdited ?? false
    }

    static var defaultURL: URL {
        AppInfo.dataFolder
            .appendingPathComponent("Descriptions.json")
    }

    func description(forKey key: String) -> String? {
        lock.lock()
        defer { lock.unlock() }
        return entries[key]?.text
    }

    /// The saved description with where it came from.
    func record(forKey key: String) -> DescriptionRecord? {
        lock.lock()
        defer { lock.unlock() }
        return entries[key]
    }

    /// Saves a description. `edited` marks one the person wrote or corrected: the record keeps how Apple Intelligence
    /// made the description it corrects, and its first text as `original`. Otherwise it's a new description, made as
    /// `provenance` says, which replaces the record (and the mark) whole.
    func remember(_ text: String, forKey key: String, edited isEdited: Bool = false,
                  provenance: DescriptionProvenance? = nil) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        lock.lock()
        if isEdited {
            entries[key] = Self.corrected(entries[key], to: trimmed, at: provenance?.created ?? Date())
        } else {
            entries[key] = provenance?.record(trimmed)
                ?? DescriptionRecord(text: trimmed, created: DescriptionProvenance.timestamp(Date()))
        }
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

    /// The person's correction of `saved` (nil when there was no description): Apple Intelligence's details and first
    /// text are kept, and it's marked as theirs, with when.
    static func corrected(_ saved: DescriptionRecord?, to text: String, at date: Date) -> DescriptionRecord {
        let when = DescriptionProvenance.timestamp(date)
        guard var record = saved else {
            var record = DescriptionProvenance(source: DescriptionProvenance.person, created: date).record(text)
            record.edited = when
            return record
        }
        if !record.isEdited { record.original = record.text }
        record.text = text
        record.source = DescriptionProvenance.person
        record.edited = when
        return record
    }

    /// Keeps a file's description when My Photo Library Described itself writes into the file (a caption or alt text, #31), which
    /// changes its size but not its picture, as Windows' DescriptionStore.Rekey does. What's on screen is told, but
    /// not as a new description, so it isn't written into the caption again.
    func rekey(from old: String, to new: String) {
        guard old != new else { return }
        lock.lock()
        guard let record = entries.removeValue(forKey: old) else {
            lock.unlock()
            return
        }
        entries[new] = record
        let schedule = !saveScheduled
        saveScheduled = true
        lock.unlock()
        if schedule {
            DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 2) { [self] in save() }
        }
        DispatchQueue.main.async {
            NotificationCenter.default.post(name: Self.didChange, object: self)
        }
    }

    var count: Int {
        lock.lock()
        defer { lock.unlock() }
        return entries.count
    }

    func description(for asset: PHAsset) -> String? { description(forKey: asset.localIdentifier) }

    func remember(_ text: String, for asset: PHAsset, provenance: DescriptionProvenance? = nil) {
        remember(text, forKey: asset.localIdentifier, provenance: provenance)
    }

    func save() {
        lock.lock()
        saveScheduled = false
        let snapshot = entries
        lock.unlock()
        guard let data = try? JSONEncoder().encode(File(version: Self.fileVersion, descriptions: snapshot)) else { return }
        try? FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
    }
}
