import Foundation

/// Runs whisper.cpp's `whisper-server` locally so the model stays loaded between dictations.
final class WhisperServer {
    enum Failure: LocalizedError {
        case binaryNotFound, modelNotFound(URL), notReady, badResponse(String)

        var errorDescription: String? {
            switch self {
            case .binaryNotFound: return "whisper-server não encontrado (brew install whisper-cpp)"
            case .modelNotFound: return "Modelo não baixado — escolha um em Modelos"
            case .notReady: return "whisper-server não respondeu"
            case .badResponse(let body): return "Resposta inválida do whisper-server: \(body)"
            }
        }
    }

    private let port = 8178
    private var process: Process?
    private var baseURL: URL { URL(string: "http://127.0.0.1:\(port)")! }

    private static let searchPaths = ["/opt/homebrew/bin/whisper-server", "/usr/local/bin/whisper-server"]

    /// Whisper models need whisper.cpp from Homebrew; the others run inside the app.
    static var isInstalled: Bool { searchPaths.contains(where: FileManager.default.isExecutableFile) }

    func start() async throws {
        if process?.isRunning == true { return }
        guard let binary = Self.searchPaths.first(where: FileManager.default.isExecutableFile) else {
            throw Failure.binaryNotFound
        }
        let model = Settings.modelPath
        guard FileManager.default.fileExists(atPath: model.path) else { throw Failure.modelNotFound(model) }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: binary)
        process.arguments = [
            "-m", model.path,
            "-l", "auto",
            "--host", "127.0.0.1",
            "--port", String(port),
            "-t", String(max(4, ProcessInfo.processInfo.activeProcessorCount - 2)),
        ]
        let log = Settings.supportDirectory.appendingPathComponent("whisper-server.log")
        FileManager.default.createFile(atPath: log.path, contents: nil)
        let handle = try FileHandle(forWritingTo: log)
        process.standardOutput = handle
        process.standardError = handle
        try process.run()
        self.process = process

        for _ in 0..<120 {
            if await isReachable() { return }
            try await Task.sleep(for: .milliseconds(500))
        }
        throw Failure.notReady
    }

    func stop() {
        process?.terminate()
        process = nil
    }

    /// Restarts with the currently selected model, waiting for the old server to release the port.
    func restart() async throws {
        let old = process
        stop()
        while old?.isRunning == true { try await Task.sleep(for: .milliseconds(50)) }
        try await start()
    }

    func transcribe(wav: Data, language: String, prompt: String? = nil) async throws -> String {
        if process?.isRunning != true { try await start() }

        var form = MultipartForm()
        form.field("response_format", "json")
        form.field("temperature", "0.0")
        form.field("language", language)
        if let prompt { form.field("prompt", prompt) }
        form.file("file", filename: "audio.wav", mimeType: "audio/wav", data: wav)

        var request = URLRequest(url: baseURL.appendingPathComponent("inference"))
        request.httpMethod = "POST"
        request.timeoutInterval = 120
        request.setValue(form.contentType, forHTTPHeaderField: "Content-Type")
        request.httpBody = form.finalized()

        let (data, _) = try await URLSession.shared.data(for: request)
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let text = json["text"] as? String else {
            throw Failure.badResponse(String(decoding: data.prefix(300), as: UTF8.self))
        }
        return Self.clean(text)
    }

    private func isReachable() async -> Bool {
        var request = URLRequest(url: baseURL)
        request.timeoutInterval = 1
        let response = try? await URLSession.shared.data(for: request)
        return (response?.1 as? HTTPURLResponse)?.statusCode == 200
    }

    /// Drops Whisper's non-speech annotations such as "[BLANK_AUDIO]" or "(música)".
    private static func clean(_ text: String) -> String {
        text.replacingOccurrences(of: #"\[[^\]]*\]|\([^)]*\)"#, with: "", options: .regularExpression)
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
