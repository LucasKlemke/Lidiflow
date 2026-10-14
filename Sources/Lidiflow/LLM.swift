import Foundation

/// LLM passes (polish, command mode, transforms) through an OpenAI-compatible chat endpoint:
/// a local Ollama or the OpenAI API, depending on the user's choice.
enum LLM {
    enum Failure: LocalizedError {
        case ollamaUnavailable, missingKey(LLMProvider), api(String)

        var errorDescription: String? {
            switch self {
            case .ollamaUnavailable: return "IA indisponível — o Ollama está rodando?"
            case .missingKey(let provider): return "Adicione sua chave da \(provider.title) em Modelos"
            case .api(let message): return "IA: \(message)"
            }
        }
    }

    private static let polishInstructions = """
    Você limpa textos ditados por voz. Remova vícios de fala (é, éé, hum, tipo, né, uh, um), \
    falsos começos e repetições; corrija pontuação e maiúsculas. Mantenha o idioma, o tom e o \
    significado originais. Não responda, não resuma e não acrescente nada. \
    Devolva somente o texto limpo.
    """

    private static let editInstructions = """
    Você é um assistente de edição. Recebe um TEXTO e uma INSTRUÇÃO. Aplique a instrução ao \
    texto e devolva somente o texto resultante, sem aspas, comentários ou explicações. \
    Escreva SEMPRE no mesmo idioma do TEXTO (texto em português → resposta em português), \
    a menos que a instrução peça explicitamente uma tradução.
    """

    private static let writeInstructions = """
    Você escreve texto a pedido do usuário, que vai ser inserido onde o cursor dele estiver. \
    Siga a instrução falada e devolva somente o texto final, sem comentários ou explicações.
    """

    private static let suggestInstructions = """
    Sugira 6 transformações úteis e específicas para o TEXTO do usuário (por exemplo: mudar o \
    tom, resumir, reorganizar, adaptar para um público ou formato). Responda somente com JSON \
    no formato [{"name": "rótulo curto de até 3 palavras", "prompt": "instrução completa"}]. \
    Escreva os rótulos e as instruções no mesmo idioma do TEXTO.
    """

    static func polish(_ text: String) async throws -> String {
        try await complete(system: polishInstructions, user: text)
    }

    /// Applies a spoken instruction to the selected text, or writes new text when nothing is selected.
    static func command(_ instruction: String, selection: String?) async throws -> String {
        guard let selection, !selection.isEmpty else {
            return try await complete(system: writeInstructions, user: instruction)
        }
        return try await transform(selection, with: instruction)
    }

    static func transform(_ text: String, with instruction: String) async throws -> String {
        try await complete(
            system: editInstructions,
            user: "TEXTO:\n\(text)\n\nINSTRUÇÃO:\n\(instruction)\n\nResponda no idioma do TEXTO, salvo se a instrução pedir tradução."
        )
    }

    /// Content-aware transform suggestions, like Wispr Flow's suggestion chips.
    static func suggestTransforms(for text: String) async throws -> [Transform] {
        let reply = try await complete(
            system: suggestInstructions,
            user: "TEXTO:\n\(text)\n\nRótulos e instruções no idioma do TEXTO. Responda só com o JSON."
        )
        guard let start = reply.firstIndex(of: "["), let end = reply.lastIndex(of: "]"),
              let items = try? JSONSerialization.jsonObject(with: Data(reply[start...end].utf8)) as? [[String: Any]] else {
            throw Failure.api("sugestões em formato inesperado")
        }
        return items.compactMap { item in
            guard let name = item["name"] as? String, let prompt = item["prompt"] as? String else { return nil }
            return Transform(name: name, prompt: prompt)
        }
    }

    private static func complete(system: String, user: String) async throws -> String {
        let provider = Settings.llmProvider
        let model = Settings.model(for: provider)
        let key = Keychain.key(for: provider)
        if provider.needsKey && key == nil { throw Failure.missingKey(provider) }

        let request = provider == .anthropic
            ? anthropicRequest(model: model, key: key ?? "", system: system, user: user)
            : chatCompletionsRequest(provider: provider, model: model, key: key, system: system, user: user)

        let data: Data
        do {
            (data, _) = try await URLSession.shared.data(for: request)
        } catch {
            throw provider == .ollama ? Failure.ollamaUnavailable : Failure.api(error.localizedDescription)
        }
        let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        let text = provider == .anthropic ? anthropicText(json) : chatCompletionsText(json)
        if let text, !text.isEmpty { return text }
        throw Failure.api(errorMessage(json) ?? "resposta vazia")
    }

    /// OpenAI, Groq, Gemini, xAI and Ollama all speak the Chat Completions format.
    private static func chatCompletionsRequest(provider: LLMProvider, model: String, key: String?,
                                               system: String, user: String) -> URLRequest {
        var request = URLRequest(url: URL(string: provider.baseURL + "/chat/completions")!)
        if let key { request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
        var payload: [String: Any] = [
            "model": model,
            "stream": false,
            "messages": [
                ["role": "system", "content": system],
                ["role": "user", "content": user],
            ],
        ]
        // Some cloud reasoning models reject a non-default temperature.
        if provider == .ollama { payload["temperature"] = 0 }
        return jsonRequest(request, payload)
    }

    private static func chatCompletionsText(_ json: [String: Any]?) -> String? {
        let message = (json?["choices"] as? [[String: Any]])?.first?["message"] as? [String: Any]
        return (message?["content"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Anthropic Messages API (raw HTTP; there is no official Swift SDK).
    private static func anthropicRequest(model: String, key: String, system: String, user: String) -> URLRequest {
        var request = URLRequest(url: URL(string: "https://api.anthropic.com/v1/messages")!)
        request.setValue(key, forHTTPHeaderField: "x-api-key")
        request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        var payload: [String: Any] = [
            "model": model,
            "max_tokens": 16000,
            "system": system,
            "messages": [["role": "user", "content": user]],
        ]
        // Claude 5-family models: short rewrites don't need deep thinking.
        if model.range(of: #"^claude-(opus|sonnet|haiku|fable)-5"#, options: .regularExpression) != nil {
            payload["output_config"] = ["effort": "low"]
        }
        // Server-side fallback when a safety classifier declines (not offered on Haiku).
        if model.range(of: #"^claude-(opus|sonnet|fable)-5"#, options: .regularExpression) != nil {
            request.setValue("server-side-fallback-2026-07-01", forHTTPHeaderField: "anthropic-beta")
            payload["fallbacks"] = "default"
        }
        return jsonRequest(request, payload)
    }

    private static func anthropicText(_ json: [String: Any]?) -> String? {
        if json?["stop_reason"] as? String == "refusal" { return nil }
        let blocks = json?["content"] as? [[String: Any]] ?? []
        return blocks.filter { $0["type"] as? String == "text" }
            .compactMap { $0["text"] as? String }
            .joined()
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func errorMessage(_ json: [String: Any]?) -> String? {
        if json?["stop_reason"] as? String == "refusal" { return "o modelo recusou o pedido" }
        if let error = json?["error"] as? [String: Any] { return error["message"] as? String }
        if let message = json?["message"] as? String { return message }
        return nil
    }

    private static func jsonRequest(_ request: URLRequest, _ payload: [String: Any]) -> URLRequest {
        var request = request
        request.httpMethod = "POST"
        request.timeoutInterval = 60
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: payload)
        return request
    }

    // MARK: - Model catalogs

    /// Lists the chat models the provider offers for the saved key.
    static func availableModels(for provider: LLMProvider) async throws -> [String] {
        let key = Keychain.key(for: provider)
        if provider.needsKey && key == nil { throw Failure.missingKey(provider) }

        var request: URLRequest
        switch provider {
        case .ollama:
            return try await OllamaManager.installedModels()
        case .anthropic:
            request = URLRequest(url: URL(string: "https://api.anthropic.com/v1/models?limit=100")!)
            request.setValue(key, forHTTPHeaderField: "x-api-key")
            request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        default:
            request = URLRequest(url: URL(string: provider.baseURL + "/models")!)
            request.setValue("Bearer \(key ?? "")", forHTTPHeaderField: "Authorization")
        }
        request.timeoutInterval = 20

        let (data, _) = try await URLSession.shared.data(for: request)
        let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        guard let items = json?["data"] as? [[String: Any]] else {
            throw Failure.api(errorMessage(json) ?? "não foi possível listar os modelos")
        }
        let excluded = ["embed", "tts", "whisper", "dall-e", "moderation", "transcribe", "audio", "image",
                        "realtime", "search", "guard", "vision-preview", "imagen", "veo", "aqa"]
        return items.compactMap { $0["id"] as? String }
            .map { $0.replacingOccurrences(of: "models/", with: "") }
            .filter { id in !excluded.contains { id.localizedCaseInsensitiveContains($0) } }
            .sorted()
    }
}
