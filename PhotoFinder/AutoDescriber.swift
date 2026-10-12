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
    private var runTask: Task<Void, Never>?
    private var running = false
    /// The photo whose description was last spoken, so coming back to the list doesn't say it again.
    private var spokenID: String?

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
            self?.wait = nil
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
        speak("Photos are described automatically")
        lastID = nil
        moved(to: currentID)
    }

    /// Back on the list or gallery after Get Info, a sheet or another app: the photo you're on is described if
    /// it wasn't spoken already (you may have left during the pause). Windows' Refocused.
    func refocused(currentID: String?) {
        guard let currentID, currentID != spokenID else { return }
        lastID = nil
        moved(to: currentID)
    }

    /// Describe Photo is about to describe this photo itself: drop it from the pause and the queue, so it isn't
    /// described and spoken twice.
    func describingManually(_ id: String) {
        if lastID == id { cancelWait() }
        if target == id { target = nil }
    }

    private func settled(on id: String) {
        guard isEnabled, isStillOn(id) else { return }
        if let text = saved(id) {
            say(text, about: id)
            return
        }
        guard unavailableReason() == nil, id != describingID else { return }
        target = id
        if !running {
            // Marked before the task starts, so a second photo settling meanwhile can't start a second loop.
            running = true
            runTask = Task { await run() }
        }
    }

    private func run() async {
        defer { running = false; runTask = nil }
        while let id = target {
            target = nil
            // Moved on while the last one was being described: nothing to do until you settle again.
            guard isEnabled, isStillOn(id) else { continue }
            // Described meanwhile (by Describe Photo, say): just say it.
            if let text = saved(id) {
                say(text, about: id)
                continue
            }
            describingID = id
            let text = await describe(id)
            describingID = nil
            if let text, isEnabled, isStillOn(id) { say(text, about: id) }
        }
    }

    /// Describe Photo or Describe Again spoke this photo's description, so coming back to the window doesn't say it
    /// again.
    func spoken(_ id: String) { spokenID = id }

    private func say(_ text: String, about id: String) {
        spokenID = id
        speak(text)
    }

    /// For tests: waits until nothing is pausing or being described.
    func whenIdle() async {
        for _ in 0..<500 {
            if wait == nil, runTask == nil, !running { return }
            await wait?.value
            await runTask?.value
            if wait == nil, runTask == nil, !running { return }
            try? await Task.sleep(for: .milliseconds(5))
        }
    }

    private func cancelWait() {
        wait?.cancel()
        wait = nil
    }
}
