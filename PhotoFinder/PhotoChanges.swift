import Foundation
import Photos

/// File > Read-Only (#84, Kelly, 12 Oct): "absolutely nothing can touch a user's photo aside from reading to get
/// descriptions" until the person agrees. Every launch starts read-only, and allowing changes lasts until the app quits.
/// This is the gate every write goes through, Photos library changes and file writes alike, so it holds even if a
/// menu or a new feature forgets to ask.
enum PhotoChanges {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var _allowed = false

    /// Whether photos may be changed now. False at launch.
    static var allowed: Bool {
        get { lock.lock(); defer { lock.unlock() }; return _allowed }
        set { lock.lock(); _allowed = newValue; lock.unlock() }
    }

    /// What a command that would change a photo says while read-only (Windows' wording).
    static var readOnlyMessage: String {
        "\(AppInfo.name) is in Read-Only Mode, so photos can't be changed. To change them, turn off Read-Only Mode in the File menu."
    }

    struct ReadOnly: LocalizedError {
        var errorDescription: String? { PhotoChanges.readOnlyMessage }
    }

    /// A change to the Photos library (favorites, ratings, captions), made only when changes are allowed.
    static func performLibraryChanges(_ changes: @escaping @Sendable () -> Void) async throws {
        guard allowed else { throw ReadOnly() }
        try await PHPhotoLibrary.shared().performChanges(changes)
    }
}
