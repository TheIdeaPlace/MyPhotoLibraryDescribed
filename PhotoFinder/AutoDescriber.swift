import Foundation

/// Photo > Describe Photos Automatically: describes the photo you move to and speaks the description, so it's heard
/// without pressing Describe Photo or opening Get Info. The same behaviour as Windows' AutoDescriber:
///
/// - It waits for a short pause before speaking or describing, saved descriptions included, so the row's own name is
///   heard first and arrowing through a list doesn't start a description for every row.
/// - Only the photo you're on is described. One already being described finishes (and is saved), but it's spoken
///   only if you're still on that photo with the window in front. Then the photo you've settled on is described.
/// - It never sets up Apple Intelligence by itself, and says nothing when it isn't available unless you turned the
///   feature on yourself.
///
/// Photos, Apple Intelligence and speech are passed in, so this builds with any SDK and is tested on its own.
@MainActor
final class AutoDescriber {
    /// A saved description of the photo, if there is one.
    var saved: (String) -> String? = { _ in nil }
    /// Why describing isn't possible right now, or nil when it is. Never sets anything up.
    var unavailableReason: () -> String? = { "Describing photos needs Apple Intelligence on macOS 27 or later." }
    /// Describes the photo and saves the description. Returns what to say: the description, or why it failed.
    var describe: (String) async -> String? = { _ in nil }
    /// Whether you're still on that photo, with the window in front.
    var isStillOn: (String) -> Bool = { _ in false }
    /// Speaks, queued behind other speech.
    var speak: (String) -> Void = { _ in }

    let pause: Duration

    var isEnabled: Bool {
        didSet { if !isEnabled { cancelWait(); target = nil } }
    }

    /// The photo being described right now, so Describe Photo doesn't describe it a second time.
    private(set) var describingID: String?
    /// The photo last moved to. The selection is set again whenever the list refreshes; that isn't moving.
    private var lastID: String?
    /// The photo to describe once the current one is done.
    private var target: String?
    private var wait: Task<Void, Never>?
    private var running = false

    init(isEnabled: Bool, pause: Duration = .milliseconds(700)) {
        self.isEnabled = isEnabled
        self.pause = pause
    }

    /// The photo you're on changed. Nil when you're on nothing, a folder, a video or more than one item.
    func moved(to id: String?) {
        guard id != lastID else { return }
        lastID = id
        cancelWait()
        guard isEnabled, let id else { return }
        wait = Task { [weak self, pause] in
            try? await Task.sleep(for: pause)
            guard !Task.isCancelled else { return }
            self?.settled(on: id)
        }
    }

    /// Turned on from the menu: says why when it can't work, and describes the photo you're on.
    func turnedOn(currentID: String?) {
        isEnabled = true
        if let reason = unavailableReason() {
            speak(reason)
            return
        }
        lastID = nil
        moved(to: currentID)
    }

    private func settled(on id: String) {
        guard isEnabled, isStillOn(id) else { return }
        if let text = saved(id) {
            speak(text)
            return
        }
        guard unavailableReason() == nil, id != describingID else { return }
        target = id
        if !running { Task { await run() } }
    }

    private func run() async {
        running = true
        defer { running = false }
        while let id = target {
            target = nil
            // Moved on while the last one was being described: nothing to do until you settle again.
            guard isEnabled, isStillOn(id) else { continue }
            describingID = id
            let text = await describe(id)
            describingID = nil
            if let text, isEnabled, isStillOn(id) { speak(text) }
        }
    }

    private func cancelWait() {
        wait?.cancel()
        wait = nil
    }
}
