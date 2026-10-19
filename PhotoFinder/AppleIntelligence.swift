import CoreGraphics
import FoundationModels
import ImageIO
import Photos
import SwiftUI

#if !PF_SDK26  // macOS 27 SDK only; Mac CI defines PF_SDK26 while its Xcode is older

/// A conversation about one photo with Apple's on-device Foundation Model (macOS 27 accepts images).
/// The first request describes the photo; later questions reuse the same session, so the model
/// still has the image and its earlier answers in context.
@available(macOS 27, *)
@MainActor
@Observable
final class ImageConversation {
    struct Exchange: Identifiable {
        let id = UUID()
        let question: String?
        let answer: String
    }

    private(set) var exchanges: [Exchange] = []
    private(set) var isWorking = false
    var errorMessage: String?

    private let image: CGImage
    /// For trying once more at a smaller size.
    private let imageData: Data
    private var session: LanguageModelSession?
    /// Get Info describing or answering, so background describing waits rather than competing for the model.
    private(set) static var inProgress = 0
    /// A description saved earlier (by Describe Image or background describing), shown without asking again.
    private let saved: String?

    /// Whether this started from a saved description rather than describing.
    var startedFromSaved: Bool { saved != nil }
    private let onDescribed: (String, DescriptionProvenance) -> Void

    static let instructions = """
        You describe photos for a person who is blind and uses a screen reader. \
        Be accurate and concrete, and describe only what is visible. \
        If something is unclear, say so rather than guessing. \
        Do not identify people by name. Do not begin with "The image shows" or "This photo shows".
        """

    /// The standard prompt, or the one chosen in Settings.
    static var describePrompt: String { DescriptionPrompt.current }

    init?(imageData: Data, saved: String? = nil,
          onDescribed: @escaping (String, DescriptionProvenance) -> Void = { _, _ in }) {
        guard let image = Self.makeImage(from: imageData) else { return nil }
        self.image = image
        self.imageData = imageData
        self.saved = saved
        self.onDescribed = onDescribed
        if let saved { exchanges = [Exchange(question: nil, answer: saved)] }
    }

    /// Why descriptions can't run on this Mac right now, or nil when they can.
    static var unavailableReason: String? {
        switch SystemLanguageModel.default.availability {
        case .available:
            return nil
        case .unavailable(.deviceNotEligible):
            return "This Mac can't run Apple Intelligence, so image descriptions aren't available."
        case .unavailable(.appleIntelligenceNotEnabled):
            return "Turn on Apple Intelligence in System Settings to describe images."
        case .unavailable(.modelNotReady):
            return "Apple Intelligence is still getting ready on this Mac. Try again in a little while."
        case .unavailable:
            return "Apple Intelligence isn't available right now."
        }
    }

    /// Apple Intelligence's availability as the frameworks report it, for Check System's details (#94).
    static var modelState: String {
        switch SystemLanguageModel.default.availability {
        case .available: "Available"
        case .unavailable(.deviceNotEligible): "Not available (deviceNotEligible)"
        case .unavailable(.appleIntelligenceNotEnabled): "Not available (appleIntelligenceNotEnabled)"
        case .unavailable(.modelNotReady): "Not available (modelNotReady)"
        case .unavailable(let reason): "Not available (\(reason))"
        }
    }

    /// The language descriptions are asked in, and whether Apple Intelligence supports it, for Check System (#94).
    static var languageText: String {
        let locale = Locale.current
        let name = locale.localizedString(forIdentifier: locale.identifier) ?? locale.identifier
        return SystemLanguageModel.default.supportsLocale(locale) ? "\(name), supported"
            : "\(name), not supported, so descriptions may fail"
    }

    /// A short name for what went wrong, for Check System's recent failures and bug reports: the error's case name
    /// ("guardrailViolation"), or its domain and code.
    static func failureCode(for error: Error) -> String {
        if error is LanguageModelSession.GenerationError || error is LanguageModelError {
            if let label = Mirror(reflecting: error).children.first?.label { return label }
            let name = String(String(describing: error).prefix { $0 != "(" && $0 != " " })
            if !name.isEmpty { return name }
        }
        let ns = error as NSError
        return "\(ns.domain) \(ns.code)"
    }

    func describe() async {
        await run(question: nil)
    }

    func ask(_ question: String) async {
        await run(question: question)
    }

    private func run(question: String?) async {
        guard !isWorking else { return }
        isWorking = true
        Self.inProgress += 1
        errorMessage = nil
        defer {
            isWorking = false
            Self.inProgress -= 1
        }
        do {
            let (answer, provenance) = try await attempt(question: question)
            let trimmed = answer.trimmingCharacters(in: .whitespacesAndNewlines)
            exchanges.append(Exchange(question: question, answer: trimmed))
            if let provenance { onDescribed(trimmed, provenance) }
            AccessibilityNotification.Announcement(trimmed).post()
        } catch {
            if question != nil, !(error is CancellationError) {
                DiagnosticLog.write("ask", "Apple Intelligence couldn't answer a question: \(Self.failureCode(for: error)): \(error.localizedDescription)")
            }
            errorMessage = Self.message(for: error, asking: question != nil)
            AccessibilityNotification.Announcement(errorMessage ?? "").post()
        }
    }

    /// One request, and for a failure that isn't about the photo or the question, one more a moment later, with the
    /// photo at 1536 pixels: Apple Intelligence may have been busy (background describing), and Windows' model fails
    /// on some large iPhone photos (#234).
    private func attempt(question: String?) async throws -> (String, DescriptionProvenance?) {
        // A follow-up in a conversation that's going sends only text, so it's asked again as it was.
        let followUp = session != nil && question != nil
        do {
            return try await request(question: question, image: image)
        } catch {
            guard Self.worthRetrying(error), !Task.isCancelled else { throw error }
            DiagnosticLog.write(question == nil ? "describe" : "ask",
                                "Trying again\(followUp ? "" : " smaller") after \(Self.failureCode(for: error))")
            try await Task.sleep(for: .seconds(2))
            if followUp { return try await request(question: question, image: image) }
            session = nil
            let smaller = await Task.detached(priority: .userInitiated) { [imageData] in
                Self.makeImage(from: imageData, maxPixelSize: 1536)
            }.value
            return try await request(question: question, image: smaller ?? image)
        }
    }

    private func request(question: String?, image: CGImage) async throws -> (String, DescriptionProvenance?) {
        if let session, let question {
            return (try await session.respond(to: question).content, nil)
        }
        let session = LanguageModelSession(instructions: Self.instructions)
        self.session = session
        // A question about a saved description starts a new conversation that knows what was said.
        let prompt: String
        if let question, let saved {
            prompt = "Earlier you described this photo like this: \(saved)\n\nNow answer this question about it: \(question)"
        } else {
            prompt = question ?? Self.describePrompt
        }
        // A first description is counted for Check System; a question about a saved one isn't a description.
        if question == nil {
            let start = ContinuousClock.now
            let answer = try await PhotoDescriber.respond(session, prompt: prompt, image: image)
            return (answer, .appleIntelligence(prompt: prompt, seconds: PhotoDescriber.seconds(since: start)))
        }
        let answer = try await session.respond {
            prompt
            Attachment(image)
        }.content
        return (answer, nil)
    }

    /// Not a refusal, a conversation that's too long or an unsupported language, which asking again won't change.
    static func worthRetrying(_ error: Error) -> Bool {
        if error is CancellationError { return false }
        if let error = error as? LanguageModelSession.GenerationError {
            switch error {
            case .guardrailViolation, .refusal, .exceededContextWindowSize, .unsupportedLanguageOrLocale: return false
            default: return true
            }
        }
        if let error = error as? LanguageModelError {
            switch error {
            case .guardrailViolation, .refusal, .contextSizeExceeded, .unsupportedLanguageOrLocale: return false
            default: return true
            }
        }
        return true
    }

    /// `asking`: a follow-up question, rather than describing.
    static func message(for error: Error, asking: Bool = false) -> String {
        let declined = "Apple Intelligence declined to describe this image."
        let tooLong = "This conversation is too long for Apple Intelligence. Close Get Info and open it again to start over."
        let language = "Apple Intelligence doesn't support your current language for this."
        if let error = error as? LanguageModelSession.GenerationError {
            switch error {
            case .guardrailViolation, .refusal: return declined
            case .exceededContextWindowSize: return tooLong
            case .unsupportedLanguageOrLocale: return language
            default: break
            }
        } else if let error = error as? LanguageModelError {
            switch error {
            case .guardrailViolation, .refusal: return declined
            case .contextSizeExceeded: return tooLong
            case .unsupportedLanguageOrLocale: return language
            default: break
            }
        }
        // Help > Open Log Folder has the details; the error's own text (a framework's domain and number) means nothing
        // read aloud.
        if asking { return "Apple Intelligence couldn't answer that. Try asking again in a moment." }
        return "Apple Intelligence couldn't describe this image: \(error.localizedDescription)"
    }

    /// Decodes the photo at a sensible size, already rotated upright. The model resizes internally,
    /// so a full-resolution decode would only cost memory.
    nonisolated static func makeImage(from data: Data, maxPixelSize: Int = 2048) -> CGImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixelSize,
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }
}

/// Photo > Describe Photo: one description of one photo, with the prompt from Settings. On the main actor, like
/// ImageConversation, whose instructions and prompt it uses; the decode is detached and the model call suspends.
@available(macOS 27, *)
@MainActor
enum PhotoDescriber {
    static func describe(_ data: Data, prompt: String) async throws -> String? {
        // Decoded off the main actor: a 2048-pixel decode would stall the window.
        let image = await Task.detached(priority: .userInitiated) { ImageConversation.makeImage(from: data) }.value
        guard let image else { return nil }
        // Dropped while decoding: don't ask the model, which another photo may be waiting for.
        try Task.checkCancellation()
        let session = LanguageModelSession(instructions: ImageConversation.instructions)
        let answer = try await respond(session, prompt: prompt, image: image)
        return answer.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Asks the model to describe the image, and counts the outcome for Check System (#94): how long it took, or why it
    /// failed. One photo, counted once; giving up (moving on, closing a window) isn't a failure.
    /// `countsBusy` false leaves a rate limit out of the failures: background describing waits and tries again.
    static func respond(_ session: LanguageModelSession, prompt: String, image: CGImage,
                        countsBusy: Bool = true) async throws -> String {
        let start = ContinuousClock.now
        do {
            let answer = try await session.respond {
                prompt
                Attachment(image)
            }.content
            DescribeStats.session.described(seconds: seconds(since: start))
            return answer
        } catch {
            if !(error is CancellationError), !Task.isCancelled, countsBusy || !isRateLimited(error) {
                DiagnosticLog.write("describe", "Apple Intelligence failed: \(ImageConversation.failureCode(for: error)): \(ImageConversation.message(for: error))")
                DescribeStats.session.failed(code: ImageConversation.failureCode(for: error),
                                             message: ImageConversation.message(for: error))
            }
            throw error
        }
    }

    static func isRateLimited(_ error: Error) -> Bool {
        if let error = error as? LanguageModelError, case .rateLimited = error { return true }
        if let error = error as? LanguageModelSession.GenerationError, case .rateLimited = error { return true }
        return false
    }

    static func seconds(since start: ContinuousClock.Instant) -> Double {
        let took = start.duration(to: .now).components
        return Double(took.seconds) + Double(took.attoseconds) / 1e18
    }
}

/// The Apple Intelligence part of Get Info: Describe Image, then follow-up questions.
@available(macOS 27, *)
struct AppleIntelligenceSection: View {
    let imageData: Data?
    /// The key the photo's description is saved under: a Photos identifier, or a file's key (#36).
    let descriptionKey: String
    /// Ask About Photo (#235): focus goes to the question field, and a photo with no description yet is described
    /// first, with focus already in the field, so the description is heard and the question can follow.
    let askNow: Bool
    @Environment(LibraryModel.self) private var model
    @State private var conversation: ImageConversation?
    @State private var question = ""
    @State private var askStarted = false
    @State private var askFocused = false
    @FocusState private var questionFocused: Bool

    var body: some View {
        content
            .onAppear(perform: appeared)
            .onChange(of: imageData) { appeared() }
    }

    private func appeared() {
        showSaved()
        startAsking()
    }

    /// Once the photo's data is in, for Ask About Photo. Where Apple Intelligence can't answer, Get Info is as usual
    /// and says why, as on Windows.
    private func startAsking() {
        guard askNow, !askStarted, imageData != nil else { return }
        askStarted = true
        if let reason = ImageConversation.unavailableReason {
            LibraryModel.postAnnouncement("Questions about photos need Apple Intelligence. \(reason)", queued: true)
            return
        }
        // The field takes focus itself when it's shown (below).
        if conversation == nil { start() }
    }

    @ViewBuilder private var content: some View {
        if let reason = ImageConversation.unavailableReason {
            Text(reason)
                .foregroundStyle(.secondary)
        } else {
            if let conversation {
                ForEach(conversation.exchanges) { exchange in
                    if let asked = exchange.question {
                        answerRow(label: "You asked", text: asked)
                    }
                    answerRow(label: exchange.question == nil ? "Description" : "Answer", text: exchange.answer)
                }
                if conversation.isWorking {
                    ProgressView("Asking Apple Intelligence…")
                }
                if let error = conversation.errorMessage {
                    Text(error).foregroundStyle(.red)
                }
            }
            if let conversation, conversation.startedFromSaved, !conversation.isWorking {
                // A saved description may be stale (the photo was edited) or poor: describing again replaces it.
                Button("Redescribe") {
                    // As Photo > Redescribe: a description the person corrected is replaced only if they agree.
                    let agreed = model.descriptions.isEdited(forKey: descriptionKey)
                        ? model.descriptions.description(forKey: descriptionKey) : nil
                    if agreed != nil {
                        let alert = NSAlert()
                        alert.messageText = "Replace the description you edited?"
                        alert.informativeText = "The photo will be described again, and the new description replaces yours."
                        alert.addButton(withTitle: "Redescribe")
                        alert.addButton(withTitle: "Cancel")
                        guard alert.runModal() == .alertFirstButtonReturn else { return }
                    }
                    self.conversation = nil
                    start(replacingEdit: agreed)
                }
            }
            let described = !(conversation?.exchanges.isEmpty ?? true)
            // For Ask About Photo the field stays while describing and if describing fails, as on Windows, so focus
            // isn't lost; Describe Image beside it tries again.
            if let conversation, described || askNow {
                HStack {
                    TextField("Ask a follow-up question", text: $question)
                        .focused($questionFocused)
                        .onSubmit(ask)
                        .onAppear {
                            // Once, when the field is on screen: focus set before then is dropped.
                            guard askNow, !askFocused else { return }
                            askFocused = true
                            DispatchQueue.main.async { questionFocused = true }
                        }
                    Button("Ask", action: ask)
                        .disabled(question.trimmingCharacters(in: .whitespaces).isEmpty || conversation.isWorking || !described)
                }
            }
            if !described, !(conversation?.isWorking ?? false) {
                Button("Describe Image") { start() }
                    .disabled(imageData == nil)
            }
            Text("Describes the photo with Apple Intelligence on this Mac. The photo isn't sent anywhere.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    /// A description saved earlier shows as soon as the photo's data is in, without describing again.
    private func showSaved() {
        guard conversation == nil, let imageData, let saved = model.descriptions.description(forKey: descriptionKey) else { return }
        conversation = ImageConversation(imageData: imageData, saved: saved)
    }

    private func answerRow(label: String, text: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .font(.subheadline.bold())
            Text(text)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }

    /// `replacingEdit` is the correction the person agreed Redescribe may replace; any other correction is kept.
    private func start(replacingEdit: String? = nil) {
        guard let imageData else { return }
        let cache = model.descriptions
        let key = descriptionKey
        guard let conversation = conversation ?? ImageConversation(imageData: imageData, onDescribed: { text, provenance in
            if DescriptionCache.mayReplace(edited: cache.isEdited(forKey: key), saved: cache.description(forKey: key),
                                           agreed: replacingEdit) {
                cache.remember(text, forKey: key, provenance: provenance)
            }
        }) else {
            if askNow { LibraryModel.postAnnouncement("This photo couldn't be read, so there's nothing to ask about.", queued: true) }
            return
        }
        self.conversation = conversation
        Task { await conversation.describe() }
    }

    private func ask() {
        let text = question.trimmingCharacters(in: .whitespacesAndNewlines)
        // A question typed before there's a description to ask about stays in the field until it can be asked.
        guard !text.isEmpty, let conversation, !conversation.isWorking, !conversation.exchanges.isEmpty else { return }
        question = ""
        Task { await conversation.ask(text) }
    }
}
#endif
