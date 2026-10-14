import Foundation

/// Builds a multipart/form-data request body.
struct MultipartForm {
    let boundary = "lidiflow-\(UUID().uuidString)"
    private var body = Data()

    var contentType: String { "multipart/form-data; boundary=\(boundary)" }

    mutating func field(_ name: String, _ value: String) {
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
    }

    mutating func file(_ name: String, filename: String, mimeType: String, data: Data) {
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"; filename=\"\(filename)\"\r\nContent-Type: \(mimeType)\r\n\r\n".utf8))
        body.append(data)
        body.append(Data("\r\n".utf8))
    }

    func finalized() -> Data {
        body + Data("--\(boundary)--\r\n".utf8)
    }
}

/// Cloud transcription through an OpenAI-compatible audio API (OpenAI or Groq), using the user's own key.
enum CloudTranscriber {
    enum Failure: LocalizedError {
        case missingKey(LLMProvider), api(LLMProvider, String)

        var errorDescription: String? {
            switch self {
            case .missingKey(let provider): return "Adicione sua chave da \(provider.title) em Modelos"
            case .api(let provider, let message): return "\(provider.title): \(message)"
            }
        }
    }

    static let providers: [LLMProvider] = [.openAI, .groq]

    static func models(for provider: LLMProvider) -> [String] {
        switch provider {
        case .groq: return ["whisper-large-v3-turbo", "whisper-large-v3"]
        default: return ["gpt-4o-transcribe", "gpt-4o-mini-transcribe", "whisper-1"]
        }
    }

    static func transcribe(wav: Data, language: String, prompt: String?) async throws -> String {
        let provider = Settings.transcriptionProvider
        guard let key = Keychain.key(for: provider) else { throw Failure.missingKey(provider) }

        var form = MultipartForm()
        form.field("model", Settings.transcriptionModel(for: provider))
        form.field("response_format", "json")
        if language != "auto" { form.field("language", language) }
        if let prompt { form.field("prompt", prompt) }
        form.file("file", filename: "audio.wav", mimeType: "audio/wav", data: wav)

        var request = URLRequest(url: URL(string: provider.baseURL + "/audio/transcriptions")!)
        request.httpMethod = "POST"
        request.timeoutInterval = 60
        request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        request.setValue(form.contentType, forHTTPHeaderField: "Content-Type")
        request.httpBody = form.finalized()

        let (data, _) = try await URLSession.shared.data(for: request)
        let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        if let text = json?["text"] as? String {
            return text.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        let message = (json?["error"] as? [String: Any])?["message"] as? String
        throw Failure.api(provider, message ?? "resposta inválida")
    }
}
