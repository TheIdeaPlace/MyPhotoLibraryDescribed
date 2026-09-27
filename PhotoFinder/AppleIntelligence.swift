import CoreGraphics
import FoundationModels
import ImageIO
import SwiftUI

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
    private var session: LanguageModelSession?

    private static let instructions = """
        You describe photos for a person who is blind and uses a screen reader. \
        Be accurate and concrete, and describe only what is visible. \
        If something is unclear, say so rather than guessing. \
        Do not identify people by name. Do not begin with "The image shows" or "This photo shows".
        """

    private static let describePrompt = """
        Describe this photo. Start with a one-sentence summary, then give the important details: \
        people and what they are doing, the setting, notable objects, colors and lighting, \
        and any text that appears in the photo.
        """

    init?(imageData: Data) {
        guard let image = Self.makeImage(from: imageData) else { return nil }
        self.image = image
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

    func describe() async {
        await run(question: nil)
    }

    func ask(_ question: String) async {
        await run(question: question)
    }

    private func run(question: String?) async {
        guard !isWorking else { return }
        isWorking = true
        errorMessage = nil
        defer { isWorking = false }
        do {
            let answer: String
            if let session, let question {
                answer = try await session.respond(to: question).content
            } else {
                let session = LanguageModelSession(instructions: Self.instructions)
                self.session = session
                let image = self.image
                let prompt = question ?? Self.describePrompt
                answer = try await session.respond {
                    prompt
                    Attachment(image)
                }.content
            }
            let trimmed = answer.trimmingCharacters(in: .whitespacesAndNewlines)
            exchanges.append(Exchange(question: question, answer: trimmed))
            AccessibilityNotification.Announcement(trimmed).post()
        } catch {
            errorMessage = Self.message(for: error)
            AccessibilityNotification.Announcement(errorMessage ?? "").post()
        }
    }

    private static func message(for error: Error) -> String {
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
        return "Apple Intelligence couldn't describe this image: \(error.localizedDescription)"
    }

    /// Decodes the photo at a sensible size, already rotated upright. The model resizes internally,
    /// so a full-resolution decode would only cost memory.
    private static func makeImage(from data: Data) -> CGImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: 2048,
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }
}

/// The Apple Intelligence part of Get Info: Describe Image, then follow-up questions.
@available(macOS 27, *)
struct AppleIntelligenceSection: View {
    let imageData: Data?
    @State private var conversation: ImageConversation?
    @State private var question = ""

    var body: some View {
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
            if conversation?.exchanges.isEmpty ?? true {
                if !(conversation?.isWorking ?? false) {
                    Button("Describe Image") { start() }
                        .disabled(imageData == nil)
                }
            } else if let conversation {
                HStack {
                    TextField("Ask a follow-up question", text: $question)
                        .onSubmit(ask)
                    Button("Ask", action: ask)
                        .disabled(question.trimmingCharacters(in: .whitespaces).isEmpty || conversation.isWorking)
                }
            }
            Text("Describes the photo with Apple Intelligence on this Mac. The photo isn't sent anywhere.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
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

    private func start() {
        guard let imageData else { return }
        guard let conversation = conversation ?? ImageConversation(imageData: imageData) else {
            return
        }
        self.conversation = conversation
        Task { await conversation.describe() }
    }

    private func ask() {
        let text = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, let conversation else { return }
        question = ""
        Task { await conversation.ask(text) }
    }
}
