import Foundation

/// Where polish, command mode and transforms run.
enum LLMProvider: String, CaseIterable, Identifiable {
    case ollama, openAI, anthropic, groq, gemini, xai

    var id: Self { self }

    var title: String {
        switch self {
        case .ollama: return "Ollama (local)"
        case .openAI: return "OpenAI"
        case .anthropic: return "Anthropic (Claude)"
        case .groq: return "Groq"
        case .gemini: return "Google Gemini"
        case .xai: return "xAI (Grok)"
        }
    }

    var needsKey: Bool { self != .ollama }

    /// Base URL of the OpenAI-compatible API. Anthropic uses its own Messages API.
    var baseURL: String {
        switch self {
        case .ollama: return Settings.ollamaBaseURL
        case .openAI: return "https://api.openai.com/v1"
        case .anthropic: return "https://api.anthropic.com/v1"
        case .groq: return "https://api.groq.com/openai/v1"
        case .gemini: return "https://generativelanguage.googleapis.com/v1beta/openai"
        case .xai: return "https://api.x.ai/v1"
        }
    }

    /// Starting suggestions; "Atualizar lista" fetches the provider's live catalog.
    var suggestedModels: [String] {
        switch self {
        case .ollama: return OllamaManager.catalog.map(\.id)
        case .openAI: return ["gpt-4.1-mini", "gpt-4.1", "gpt-4o-mini"]
        case .anthropic: return ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"]
        case .groq: return ["llama-3.3-70b-versatile", "llama-3.1-8b-instant"]
        case .gemini: return ["gemini-2.5-flash", "gemini-2.5-pro"]
        case .xai: return ["grok-3-mini", "grok-3"]
        }
    }

    var keyPlaceholder: String {
        switch self {
        case .openAI: return "sk-…"
        case .anthropic: return "sk-ant-…"
        case .groq: return "gsk_…"
        case .gemini: return "AIza…"
        case .xai: return "xai-…"
        case .ollama: return ""
        }
    }

    var keyURL: URL? {
        switch self {
        case .openAI: return URL(string: "https://platform.openai.com/api-keys")
        case .anthropic: return URL(string: "https://console.anthropic.com/settings/keys")
        case .groq: return URL(string: "https://console.groq.com/keys")
        case .gemini: return URL(string: "https://aistudio.google.com/apikey")
        case .xai: return URL(string: "https://console.x.ai")
        case .ollama: return nil
        }
    }
}
