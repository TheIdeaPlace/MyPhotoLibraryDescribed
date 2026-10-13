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
/// - Stepping through photos one at a time, it describes the next one ahead, in the direction you're going, while
///   you listen to this one, so it's ready when you get there. Only after you've settled on a photo (arrowing
///   quickly never looks ahead), only one photo ahead, never spoken, and given up at once if you land elsewhere:
///   the photo you're on always comes first.
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
    /// The next photo after `id`, `step` rows on (1 forward, -1 back), worth describing ahead: not described yet, not
    /// a video or folder. Nil when there's none near.
    var nextPhoto: (_ id: String, _ step: Int) -> String? = { _, _ in nil }
    /// Describes a photo ahead and saves the description, saying nothing; stops if its task is cancelled.
    var describeAhead: (String) async -> Void = { _ in }
    /// Whether it's a good time to work ahead: the window in front, not on Low Power Mode or running hot.
    var mayDescribeAhead: () -> Bool = { true }

    let pause: Duration

    var isEnabled: Bool {
        didSet { if !isEnabled { cancelWait(); cancelAhead(); target = nil } }
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
    /// How the last move went: 1 or -1 for one photo forward or back, 0 for anything else (a jump, a click).
    private var step = 0
    /// The photo being described ahead, and its work.
    private(set) var aheadID: String?
    private var aheadTask: Task<Void, Never>?
    /// Counts the work started ahead, so a dropped task finishing late can't clear a newer one's.
    private var aheadGeneration = 0

    init(isEnabled: Bool, pause: Duration = .milliseconds(700)) {
        self.isEnabled = isEnabled
        self.pause = pause
    }

    /// The photo you're on changed. Nil when you're on nothing, a folder, a video or more than one item. `step` is 1 or
    /// -1 when it's the next or previous photo, 0 otherwise.
    func moved(to id: String?, step: Int = 0) {
        guard id != lastID else { return }
        lastID = id
        self.step = abs(step) == 1 ? step : 0
        // Landed somewhere other than the photo being described ahead: that's dropped, so it can't hold this one up.
        if aheadID != nil, aheadID != id { cancelAhead() }
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
            lookAhead(from: id)
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
            let text: String?
            if aheadID == id, let ahead = aheadTask {
                // Already being described ahead: wait for that rather than start again. If it was dropped meanwhile
                // (you moved away and back) or saved nothing, it's described here after all.
                await ahead.value
                if let done = saved(id) {
                    text = done
                } else if isEnabled, isStillOn(id) {
                    text = await describe(id)
                } else {
                    text = nil
                }
            } else {
                text = await describe(id)
            }
            describingID = nil
            if let text, isEnabled, isStillOn(id) {
                say(text, about: id)
                if target == nil { lookAhead(from: id) }
            }
        }
    }

    /// Describes the next photo in the direction you're stepping, if you're stepping one at a time.
    private func lookAhead(from id: String) {
        guard isEnabled, step != 0, aheadTask == nil, mayDescribeAhead(), unavailableReason() == nil,
              let next = nextPhoto(id, step), next != describingID, saved(next) == nil else { return }
        aheadGeneration += 1
        let generation = aheadGeneration
        aheadID = next
        aheadTask = Task { [weak self] in
            await self?.describeAhead(next)
            guard let self, self.aheadGeneration == generation else { return }
            self.aheadID = nil
            self.aheadTask = nil
        }
    }

    private func cancelAhead() {
        aheadGeneration += 1
        aheadTask?.cancel()
        aheadTask = nil
        aheadID = nil
    }

    /// Describe Photo or Redescribe spoke this photo's description, so coming back to the window doesn't say it
    /// again.
    func spoken(_ id: String) { spokenID = id }

    private func say(_ text: String, about id: String) {
        spokenID = id
        speak(text)
    }

    /// For tests: waits until nothing is pausing or being described.
    func whenIdle() async {
        for _ in 0..<500 {
            if wait == nil, runTask == nil, !running, aheadTask == nil { return }
            await wait?.value
            await runTask?.value
            await aheadTask?.value
            if wait == nil, runTask == nil, !running, aheadTask == nil { return }
            try? await Task.sleep(for: .milliseconds(5))
        }
    }

    private func cancelWait() {
        wait?.cancel()
        wait = nil
    }
}
