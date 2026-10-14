import Foundation

struct OllamaModel: Identifiable, Hashable {
    let id: String
    let name: String
    let detail: String
    let size: String
}

/// Downloads and tracks open-source models in a local Ollama.
final class OllamaManager: ObservableObject {
    static let catalog = [
        OllamaModel(id: "qwen2.5:3b", name: "Qwen 2.5 3B", detail: "Rápido e bom em português", size: "1,9 GB"),
        OllamaModel(id: "llama3.2:3b", name: "Llama 3.2 3B", detail: "Leve, da Meta", size: "2,0 GB"),
        OllamaModel(id: "phi3:mini", name: "Phi-3 Mini", detail: "Compacto, da Microsoft", size: "2,2 GB"),
        OllamaModel(id: "qwen2.5:7b", name: "Qwen 2.5 7B", detail: "Melhor qualidade, mais lento", size: "4,7 GB"),
        OllamaModel(id: "llama3.1:8b", name: "Llama 3.1 8B", detail: "Equilibrado, da Meta", size: "4,9 GB"),
        OllamaModel(id: "mistral:7b", name: "Mistral 7B", detail: "Ótimo para reescrita", size: "4,1 GB"),
        OllamaModel(id: "gemma2:9b", name: "Gemma 2 9B", detail: "O mais capaz da lista, do Google", size: "5,4 GB"),
    ]

    @Published private(set) var isRunning = false
    @Published private(set) var installed: [String] = []
    @Published private(set) var progress: [String: Double] = [:]
    @Published private(set) var lastError: String?

    private var pulls: [String: Task<Void, Never>] = [:]

    private static var host: URL {
        // The settings store the OpenAI-compatible URL (…/v1); the native API lives at the root.
        let base = Settings.ollamaBaseURL.replacingOccurrences(of: "/v1", with: "")
        return URL(string: base) ?? URL(string: "http://localhost:11434")!
    }

    private static let binaryPaths = ["/opt/homebrew/bin/ollama", "/usr/local/bin/ollama"]

    var isInstalledOnMac: Bool {
        Self.binaryPaths.contains(where: FileManager.default.isExecutableFile)
    }

    @MainActor
    func refresh() async {
        var request = URLRequest(url: Self.host.appendingPathComponent("api/version"))
        request.timeoutInterval = 1.5
        isRunning = (try? await URLSession.shared.data(for: request)) != nil
        installed = isRunning ? ((try? await Self.installedModels()) ?? []) : []
    }

    func isInstalled(_ id: String) -> Bool {
        let full = id.contains(":") ? id : id + ":latest"
        return installed.contains(full) || installed.contains(id)
    }

    /// Starts `ollama serve` in the background.
    @MainActor
    func start() async {
        guard let binary = Self.binaryPaths.first(where: FileManager.default.isExecutableFile) else {
            lastError = "Ollama não está instalado (brew install ollama)"
            return
        }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: binary)
        process.arguments = ["serve"]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            lastError = "Não foi possível iniciar o Ollama"
            return
        }
        for _ in 0..<20 {
            try? await Task.sleep(for: .milliseconds(300))
            await refresh()
            if isRunning { return }
        }
    }

    @MainActor
    func pull(_ id: String) {
        guard pulls[id] == nil else { return }
        lastError = nil
        progress[id] = 0
        pulls[id] = Task { @MainActor in
            defer {
                pulls[id] = nil
                progress[id] = nil
            }
            do {
                var request = URLRequest(url: Self.host.appendingPathComponent("api/pull"))
                request.httpMethod = "POST"
                request.timeoutInterval = 3600
                request.httpBody = try JSONSerialization.data(withJSONObject: ["name": id, "stream": true])
                let (bytes, _) = try await URLSession.shared.bytes(for: request)
                // Each line is a JSON status update: {"status", "total", "completed"} or {"error"}.
                for try await line in bytes.lines {
                    guard let update = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any] else { continue }
                    if let error = update["error"] as? String {
                        lastError = "Ollama: \(error)"
                        return
                    }
                    if let total = update["total"] as? Double, total > 0, let completed = update["completed"] as? Double {
                        progress[id] = completed / total
                    }
                }
                await refresh()
            } catch is CancellationError {
                return
            } catch {
                lastError = "Download interrompido: \(error.localizedDescription)"
            }
        }
    }

    @MainActor
    func cancel(_ id: String) {
        pulls[id]?.cancel()
    }

    @MainActor
    func delete(_ id: String) async {
        var request = URLRequest(url: Self.host.appendingPathComponent("api/delete"))
        request.httpMethod = "DELETE"
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["name": id])
        _ = try? await URLSession.shared.data(for: request)
        await refresh()
    }

    static func installedModels() async throws -> [String] {
        var request = URLRequest(url: host.appendingPathComponent("api/tags"))
        request.timeoutInterval = 3
        let (data, _) = try await URLSession.shared.data(for: request)
        let json = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        return ((json?["models"] as? [[String: Any]]) ?? []).compactMap { $0["name"] as? String }.sorted()
    }
}
