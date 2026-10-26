import Foundation
#if canImport(FoundationModels)
import FoundationModels
#endif

/// Apple's on-device language model (Foundation Models, macOS 26+ with Apple Intelligence on).
/// Built into macOS: nothing to download or keep running, unlike Ollama.
enum AppleIntelligence {
    static let modelID = "apple-on-device"

    /// Whether this Mac can run it right now.
    static var isAvailable: Bool {
        #if canImport(FoundationModels)
        guard #available(macOS 26, *) else { return false }
        return SystemLanguageModel.default.isAvailable
        #else
        return false
        #endif
    }

    /// Apple Intelligence is on: the model runs now, or will once macOS finishes downloading it.
    static var isEnabled: Bool {
        #if canImport(FoundationModels)
        guard #available(macOS 26, *) else { return false }
        switch SystemLanguageModel.default.availability {
        case .available, .unavailable(.modelNotReady): return true
        default: return false
        }
        #else
        return false
        #endif
    }

    /// Whether this Mac could run it (macOS 26 on Apple Silicon), even if Apple Intelligence is off.
    static var isSupported: Bool {
        #if canImport(FoundationModels)
        guard #available(macOS 26, *) else { return false }
        if case .unavailable(.deviceNotEligible) = SystemLanguageModel.default.availability { return false }
        return true
        #else
        return false
        #endif
    }

    /// Why it can't run, in words for the user; nil when it can.
    static var unavailableReason: String? {
        #if canImport(FoundationModels)
        guard #available(macOS 26, *) else { return "Precisa do macOS 26 ou superior" }
        switch SystemLanguageModel.default.availability {
        case .available: return nil
        case .unavailable(.deviceNotEligible): return "Este Mac não tem Apple Intelligence"
        case .unavailable(.appleIntelligenceNotEnabled): return "Ative a Apple Intelligence em Ajustes do Sistema"
        case .unavailable(.modelNotReady): return "O macOS ainda está baixando o modelo"
        case .unavailable: return "Indisponível neste Mac"
        }
        #else
        return "Precisa do macOS 26 ou superior"
        #endif
    }

    static func complete(system: String, user: String) async throws -> String {
        #if canImport(FoundationModels)
        guard #available(macOS 26, *) else { throw LLM.Failure.api(unavailableReason ?? "indisponível") }
        guard SystemLanguageModel.default.isAvailable else { throw LLM.Failure.api(unavailableReason ?? "indisponível") }
        let session = LanguageModelSession(instructions: system)
        do {
            let response = try await session.respond(to: user, options: GenerationOptions(temperature: 0))
            return response.content.trimmingCharacters(in: .whitespacesAndNewlines)
        } catch let error as LanguageModelSession.GenerationError {
            switch error {
            case .exceededContextWindowSize: throw LLM.Failure.api("texto longo demais para o modelo da Apple")
            case .guardrailViolation, .refusal: throw LLM.Failure.api("o modelo da Apple recusou o pedido")
            case .unsupportedLanguageOrLocale: throw LLM.Failure.api("o modelo da Apple não fala este idioma")
            default: throw LLM.Failure.api(error.localizedDescription)
            }
        }
        #else
        throw LLM.Failure.api("Precisa do macOS 26 ou superior")
        #endif
    }
}
