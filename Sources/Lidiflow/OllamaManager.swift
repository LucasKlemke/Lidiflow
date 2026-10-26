import Foundation

struct OllamaModel: Identifiable, Hashable {
    let id: String
    let name: String
    let detail: String
    let size: String
    var recommended = false
}

/// Downloads and tracks open-source models in a local Ollama.
final class OllamaManager: ObservableObject {
    /// One per app: the model screen and the AI requests share the server it starts.
    static let shared = OllamaManager()
    static let catalog = [
        OllamaModel(id: "qwen2.5:3b", name: "Qwen 2.5 3B", detail: "Rápido e bom em português", size: "1,9 GB", recommended: true),
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
    /// Models waiting for Ollama itself to be downloaded or started before their own download begins.
    @Published private(set) var preparing: Set<String> = []
    @Published private(set) var lastError: String?
    /// 0–1 while the app downloads Ollama itself.
    @Published private(set) var installProgress: Double?

    private var pulls: [String: Task<Void, Never>] = [:]
    /// The `ollama serve` this app started, stopped when the app quits.
    private var server: Process?
    private var starting: Task<Void, Never>?

    private init() {}

    private static var host: URL {
        // The settings store the OpenAI-compatible URL (…/v1); the native API lives at the root.
        let base = Settings.ollamaBaseURL.replacingOccurrences(of: "/v1", with: "")
        return URL(string: base) ?? URL(string: "http://localhost:11434")!
    }

    /// Whether the settings point at an Ollama on this Mac, the only kind the app can start.
    static var isLocalHost: Bool {
        ["localhost", "127.0.0.1", "::1"].contains(host.host() ?? "")
    }

    /// Where the app puts the Ollama it downloads, so nobody needs Homebrew or the Terminal.
    private static var bundledDirectory: URL { Settings.supportDirectory.appendingPathComponent("ollama", isDirectory: true) }

    /// Homebrew's, the Ollama app's, then the one this app downloaded.
    private static var binaryPaths: [String] {
        ["/opt/homebrew/bin/ollama", "/usr/local/bin/ollama", "/Applications/Ollama.app/Contents/Resources/ollama",
         NSHomeDirectory() + "/Applications/Ollama.app/Contents/Resources/ollama",
         bundledDirectory.appendingPathComponent("ollama").path]
    }

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

    /// Starts Ollama if it's installed and not already running (by this app, the Ollama app or Homebrew),
    /// so nobody has to click Iniciar each time the app opens. Returns whether it's running.
    @MainActor
    @discardableResult
    func ensureRunning() async -> Bool {
        await refresh()
        guard !isRunning, isInstalledOnMac, Self.isLocalHost else { return isRunning }
        await start()
        return isRunning
    }

    /// Starts `ollama serve` in the background. Concurrent calls wait for the same start.
    @MainActor
    func start() async {
        if let starting { return await starting.value }
        let task = Task { @MainActor in await launch() }
        starting = task
        await task.value
        starting = nil
    }

    @MainActor
    private func launch() async {
        await refresh()
        if isRunning { return }
        // The one this app started is alive, just slow to answer (loading a model): wait instead of
        // killing it, which would cancel what it's doing.
        guard server?.isRunning != true else { return await waitUntilRunning() }
        guard let binary = Self.binaryPaths.first(where: FileManager.default.isExecutableFile) else {
            lastError = "Ollama não está instalado: clique em Instalar"
            return
        }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: binary)
        process.arguments = ["serve"]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
            server = process
        } catch {
            lastError = "Não foi possível iniciar o Ollama"
            return
        }
        await waitUntilRunning()
    }

    @MainActor
    private func waitUntilRunning() async {
        for _ in 0..<30 {
            try? await Task.sleep(for: .milliseconds(300))
            await refresh()
            if isRunning {
                lastError = nil
                return
            }
        }
        lastError = "O Ollama não iniciou"
    }

    func stop() {
        server?.terminate()
        server = nil
    }

    /// Downloads the official Ollama for macOS (Intel and Apple Silicon, signed by Ollama) from its GitHub
    /// releases into Application Support, then starts it.
    @MainActor
    func install() async {
        guard installProgress == nil else { return }
        lastError = nil
        installProgress = 0
        defer { installProgress = nil }
        let source = URL(string: "https://github.com/ollama/ollama/releases/latest/download/ollama-darwin.tgz")!
        do {
            let archive = try await Self.download(source) { [weak self] value in
                Task { @MainActor in if self?.installProgress != nil { self?.installProgress = value } }
            }
            defer { try? FileManager.default.removeItem(at: archive) }
            let directory = Self.bundledDirectory
            try? FileManager.default.removeItem(at: directory)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let tar = Process()
            tar.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
            tar.arguments = ["-xzf", archive.path, "-C", directory.path]
            try tar.run()
            tar.waitUntilExit()
            guard tar.terminationStatus == 0, isInstalledOnMac else {
                lastError = "Não foi possível instalar o Ollama"
                return
            }
        } catch {
            lastError = "Download do Ollama interrompido: \(error.localizedDescription)"
            return
        }
        await start()
    }

    /// A download to a temporary file, reporting 0–1 progress.
    private static func download(_ url: URL, progress: @escaping (Double) -> Void) async throws -> URL {
        let (temporary, response) = try await URLSession.shared.download(from: url, delegate: DownloadProgress(progress))
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
        // The system deletes `temporary` once this returns, so keep it under a name of our own.
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("ollama-\(UUID().uuidString).tgz")
        try FileManager.default.moveItem(at: temporary, to: file)
        return file
    }

    private final class DownloadProgress: NSObject, URLSessionDownloadDelegate {
        let report: (Double) -> Void

        init(_ report: @escaping (Double) -> Void) {
            self.report = report
        }

        func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                        totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
            guard totalBytesExpectedToWrite > 0 else { return }
            report(Double(totalBytesWritten) / Double(totalBytesExpectedToWrite))
        }

        // The async `download(from:)` delivers the file itself; this is only here to satisfy the protocol.
        func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {}
    }

    /// Downloads a model in one step: installs and starts Ollama first when needed, so the user only picks
    /// a model. `done` runs once the model is ready.
    @MainActor
    func pull(_ id: String, done: @escaping @MainActor () -> Void = {}) {
        guard pulls[id] == nil else { return }
        lastError = nil
        pulls[id] = Task { @MainActor in
            defer {
                pulls[id] = nil
                progress[id] = nil
                preparing.remove(id)
            }
            preparing.insert(id)
            if !isInstalledOnMac && Self.isLocalHost { await install() }
            guard !Task.isCancelled else { return }
            guard await ensureRunning() else {
                if lastError == nil { lastError = "O Ollama não iniciou" }
                return
            }
            preparing.remove(id)
            progress[id] = 0
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
                if isInstalled(id) { done() }
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
