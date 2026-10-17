import Foundation

/// A speech model that runs on this Mac.
struct LocalModel: Identifiable, Hashable {
    /// GGUF models (Whisper, Parakeet, Nemotron…) run in-process via transcribe.cpp, so nothing has to be
    /// installed; Apple's model runs through the Speech framework, with files macOS manages.
    enum Engine { case transcribe, apple }

    let id: String
    let name: String
    let detail: String
    let size: String
    /// 0–100, from Handy's benchmarks, for the speed × accuracy bars.
    var speed = 0
    var accuracy = 0
    var engine = Engine.transcribe
    var source: String?

    /// A transcribe.cpp model from the GGUF builds the Handy project publishes on Hugging Face.
    static func gguf(_ file: String, repo: String, name: String, detail: String, size: String,
                     speed: Int, accuracy: Int) -> LocalModel {
        LocalModel(id: file, name: name, detail: detail, size: size, speed: speed, accuracy: accuracy, engine: .transcribe,
                   source: "https://huggingface.co/handy-computer/\(repo)/resolve/main/\(file)")
    }

    var url: URL { URL(string: source ?? "https://huggingface.co/")! }
}

/// Downloads and tracks local speech models in Application Support.
final class ModelManager: NSObject, ObservableObject, URLSessionDownloadDelegate {
    static let catalog = (AppleSpeech.isSupported ? [AppleSpeech.model] : []) + [
        .gguf("whisper-base-Q8_0.gguf", repo: "whisper-base-gguf",
              name: "Whisper Base", detail: "Muito rápido, menos preciso", size: "85 MB", speed: 99, accuracy: 71),
        .gguf("whisper-small-Q8_0.gguf", repo: "whisper-small-gguf",
              name: "Whisper Small", detail: "Rápido, bom para frases curtas", size: "270 MB", speed: 78, accuracy: 80),
        .gguf("whisper-large-v3-turbo-Q5_K_M.gguf", repo: "whisper-large-v3-turbo-gguf",
              name: "Whisper Large v3 Turbo (comprimido)", detail: "Ótima precisão e rápido", size: "620 MB", speed: 40, accuracy: 87),
        .gguf("nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf", repo: "nemotron-3.5-asr-streaming-0.6b-gguf",
              name: "Nemotron Streaming 3.5", detail: "NVIDIA, 28 idiomas, muito rápido", size: "751 MB", speed: 84, accuracy: 82),
        .gguf("parakeet-tdt-0.6b-v3-Q8_0.gguf", repo: "parakeet-tdt-0.6b-v3-gguf",
              name: "Parakeet TDT 0.6B v3", detail: "NVIDIA, 25 idiomas europeus, rápido e preciso", size: "740 MB", speed: 79, accuracy: 88),
        .gguf("Qwen3-ASR-0.6B-Q8_0.gguf", repo: "Qwen3-ASR-0.6B-gguf",
              name: "Qwen3-ASR 0.6B", detail: "Alibaba, 30 idiomas, preciso", size: "850 MB", speed: 63, accuracy: 87),
        .gguf("whisper-medium-Q8_0.gguf", repo: "whisper-medium-gguf",
              name: "Whisper Medium", detail: "Preciso, mais lento", size: "832 MB", speed: 42, accuracy: 84),
        .gguf("whisper-large-v3-turbo-Q8_0.gguf", repo: "whisper-large-v3-turbo-gguf",
              name: "Whisper Large v3 Turbo", detail: "Precisão máxima do Turbo", size: "886 MB", speed: 35, accuracy: 88),
        .gguf("cohere-transcribe-03-2026-Q5_K_M.gguf", repo: "cohere-transcribe-03-2026-gguf",
              name: "Cohere Transcribe", detail: "14 idiomas, precisão máxima, mais lento", size: "1,8 GB", speed: 63, accuracy: 92),
        .gguf("whisper-large-v3-Q8_0.gguf", repo: "whisper-large-v3-gguf",
              name: "Whisper Large v3", detail: "O mais preciso, o mais lento", size: "1,7 GB", speed: 23, accuracy: 89),
    ]

    /// The one set up on first launch: Apple's when this Mac has it, otherwise Parakeet, which runs inside
    /// the app (no whisper-cpp to install) and handles Portuguese, English and Spanish well.
    static var recommended: LocalModel {
        AppleSpeech.isSupported ? AppleSpeech.model : model(id: "parakeet-tdt-0.6b-v3-Q8_0.gguf")!
    }

    /// whisper.cpp models from before 0.5, which needed `brew install whisper-cpp`, and the GGUF build
    /// of each that now runs inside the app.
    static let legacyWhisper = [
        "ggml-base.bin": "whisper-base-Q8_0.gguf",
        "ggml-small.bin": "whisper-small-Q8_0.gguf",
        "ggml-large-v3-turbo-q5_0.bin": "whisper-large-v3-turbo-Q5_K_M.gguf",
        "ggml-medium.bin": "whisper-medium-Q8_0.gguf",
        "ggml-large-v3-turbo.bin": "whisper-large-v3-turbo-Q8_0.gguf",
        "ggml-large-v3.bin": "whisper-large-v3-Q8_0.gguf",
    ]

    static func model(id: String) -> LocalModel? {
        catalog.first { $0.id == id }
    }

    /// The local model chosen in Modelos.
    static var selected: LocalModel? { model(id: Settings.localModel) }

    @Published private(set) var installed: Set<String> = []
    @Published private(set) var progress: [String: Double] = [:]
    @Published private(set) var lastError: String?
    /// Called on the main queue after a model finishes downloading, with its id.
    var onInstalled: (String) -> Void = { _ in }

    private var tasks: [String: URLSessionDownloadTask] = [:]
    private var appleInstall: Task<Void, Never>?
    private lazy var session = URLSession(configuration: .default, delegate: self, delegateQueue: .main)

    override init() {
        super.init()
        migrateLegacyWhisper()
        refresh()
    }

    /// Someone who used an old whisper.cpp model gets its GGUF build downloaded in its place, and the
    /// old files (up to 3 GB, unusable now) are deleted.
    private func migrateLegacyWhisper() {
        let directory = Settings.modelsDirectory
        let legacy = Self.legacyWhisper.keys.filter {
            FileManager.default.fileExists(atPath: directory.appendingPathComponent($0).path)
        }
        guard !legacy.isEmpty else { return }
        if let selected = Self.selected, !FileManager.default.fileExists(atPath: directory.appendingPathComponent(selected.id).path) {
            DispatchQueue.main.async { self.download(selected) }
        }
        legacy.forEach { try? FileManager.default.removeItem(at: directory.appendingPathComponent($0)) }
    }

    func refresh() {
        installed = Set(Self.catalog.filter { $0.engine != .apple }.map(\.id).filter {
            FileManager.default.fileExists(atPath: Settings.modelsDirectory.appendingPathComponent($0).path)
        })
        guard AppleSpeech.isSupported else { return }
        // Per language: switching to one macOS hasn't downloaded shows "Baixar" again.
        Task { @MainActor in
            if await AppleSpeech.isInstalled(language: Settings.language) {
                installed.insert(AppleSpeech.model.id)
            } else {
                installed.remove(AppleSpeech.model.id)
            }
        }
    }

    func download(_ model: LocalModel) {
        if model.engine == .apple { return installApple() }
        guard tasks[model.id] == nil else { return }
        lastError = nil
        let task = session.downloadTask(with: model.url)
        task.taskDescription = model.id
        tasks[model.id] = task
        progress[model.id] = 0
        task.resume()
    }

    func cancel(_ model: LocalModel) {
        if model.engine == .apple {
            appleInstall?.cancel()
            appleInstall = nil
            progress.removeValue(forKey: model.id)
            return
        }
        tasks.removeValue(forKey: model.id)?.cancel()
        progress.removeValue(forKey: model.id)
    }

    func delete(_ model: LocalModel) {
        try? FileManager.default.removeItem(at: Settings.modelsDirectory.appendingPathComponent(model.id))
        refresh()
    }

    /// macOS downloads Apple's model for the current language; the app only asks and shows the progress.
    private func installApple() {
        guard appleInstall == nil else { return }
        let id = AppleSpeech.model.id
        lastError = nil
        progress[id] = 0
        appleInstall = Task { @MainActor in
            do {
                try await AppleSpeech.install(language: Settings.language) { value in
                    Task { @MainActor in if self.progress[id] != nil { self.progress[id] = value } }
                }
            } catch where !Task.isCancelled {
                lastError = error.localizedDescription
            } catch {}
            appleInstall = nil
            progress.removeValue(forKey: id)
            refresh()
            if await AppleSpeech.isInstalled(language: Settings.language) { onInstalled(id) }
        }
    }

    // MARK: - URLSessionDownloadDelegate

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard let id = downloadTask.taskDescription, totalBytesExpectedToWrite > 0 else { return }
        progress[id] = Double(totalBytesWritten) / Double(totalBytesExpectedToWrite)
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        guard let id = downloadTask.taskDescription else { return }
        guard (downloadTask.response as? HTTPURLResponse)?.statusCode == 200 else {
            lastError = "Falha ao baixar \(id)"
            return
        }
        // The temporary file is deleted when this method returns, so move it now.
        let destination = Settings.modelsDirectory.appendingPathComponent(id)
        try? FileManager.default.removeItem(at: destination)
        do {
            try FileManager.default.moveItem(at: location, to: destination)
            refresh()
            onInstalled(id)
        } catch {
            lastError = "Não foi possível salvar \(id)"
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let id = task.taskDescription else { return }
        tasks.removeValue(forKey: id)
        progress.removeValue(forKey: id)
        if let error = error as? URLError, error.code != .cancelled {
            lastError = "Download interrompido: \(error.localizedDescription)"
        }
        refresh()
    }
}
