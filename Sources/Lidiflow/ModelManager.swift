import Foundation

/// A speech model that runs on this Mac.
struct LocalModel: Identifiable, Hashable {
    /// whisper.cpp ggml models run in `whisper-server`; GGUF models run in-process via transcribe.cpp.
    enum Engine { case whisper, transcribe }

    let id: String
    let name: String
    let detail: String
    let size: String
    /// 0–100, from Handy's benchmarks, for the speed × accuracy bars.
    var speed = 0
    var accuracy = 0
    var engine = Engine.whisper
    var source: String?

    /// A transcribe.cpp model from the GGUF builds the Handy project publishes on Hugging Face.
    static func gguf(_ file: String, repo: String, name: String, detail: String, size: String,
                     speed: Int, accuracy: Int) -> LocalModel {
        LocalModel(id: file, name: name, detail: detail, size: size, speed: speed, accuracy: accuracy, engine: .transcribe,
                   source: "https://huggingface.co/handy-computer/\(repo)/resolve/main/\(file)")
    }

    var url: URL { URL(string: source ?? "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/\(id)")! }
}

/// Downloads and tracks local speech models in Application Support.
final class ModelManager: NSObject, ObservableObject, URLSessionDownloadDelegate {
    static let catalog = [
        LocalModel(id: "ggml-base.bin", name: "Base", detail: "Muito rápido, menos preciso", size: "142 MB", speed: 99, accuracy: 71),
        LocalModel(id: "ggml-small.bin", name: "Small", detail: "Rápido, bom para frases curtas", size: "466 MB", speed: 78, accuracy: 80),
        LocalModel(id: "ggml-large-v3-turbo-q5_0.bin", name: "Large v3 Turbo (comprimido)", detail: "Recomendado: ótima precisão e rápido", size: "547 MB", speed: 40, accuracy: 87),
        .gguf("nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf", repo: "nemotron-3.5-asr-streaming-0.6b-gguf",
              name: "Nemotron Streaming 3.5", detail: "NVIDIA, 28 idiomas, muito rápido", size: "751 MB", speed: 84, accuracy: 82),
        .gguf("parakeet-tdt-0.6b-v3-Q8_0.gguf", repo: "parakeet-tdt-0.6b-v3-gguf",
              name: "Parakeet TDT 0.6B v3", detail: "NVIDIA, 25 idiomas europeus, rápido e preciso", size: "740 MB", speed: 79, accuracy: 88),
        .gguf("Qwen3-ASR-0.6B-Q8_0.gguf", repo: "Qwen3-ASR-0.6B-gguf",
              name: "Qwen3-ASR 0.6B", detail: "Alibaba, 30 idiomas, preciso", size: "850 MB", speed: 63, accuracy: 87),
        LocalModel(id: "ggml-medium.bin", name: "Medium", detail: "Preciso, mais lento", size: "1,5 GB", speed: 42, accuracy: 84),
        LocalModel(id: "ggml-large-v3-turbo.bin", name: "Large v3 Turbo", detail: "Precisão máxima do Turbo", size: "1,6 GB", speed: 35, accuracy: 88),
        .gguf("cohere-transcribe-03-2026-Q5_K_M.gguf", repo: "cohere-transcribe-03-2026-gguf",
              name: "Cohere Transcribe", detail: "14 idiomas, precisão máxima, mais lento", size: "1,8 GB", speed: 63, accuracy: 92),
        LocalModel(id: "ggml-large-v3.bin", name: "Large v3", detail: "O mais preciso, o mais lento", size: "3,1 GB", speed: 23, accuracy: 89),
    ]

    static func model(id: String) -> LocalModel? {
        catalog.first { $0.id == id }
    }

    /// The local model chosen in Modelos.
    static var selected: LocalModel? { model(id: Settings.localModel) }

    @Published private(set) var installed: Set<String> = []
    @Published private(set) var progress: [String: Double] = [:]
    @Published private(set) var lastError: String?

    private var tasks: [String: URLSessionDownloadTask] = [:]
    private lazy var session = URLSession(configuration: .default, delegate: self, delegateQueue: .main)

    override init() {
        super.init()
        refresh()
    }

    func refresh() {
        installed = Set(Self.catalog.map(\.id).filter {
            FileManager.default.fileExists(atPath: Settings.modelsDirectory.appendingPathComponent($0).path)
        })
    }

    func download(_ model: LocalModel) {
        guard tasks[model.id] == nil else { return }
        lastError = nil
        let task = session.downloadTask(with: model.url)
        task.taskDescription = model.id
        tasks[model.id] = task
        progress[model.id] = 0
        task.resume()
    }

    func cancel(_ model: LocalModel) {
        tasks.removeValue(forKey: model.id)?.cancel()
        progress.removeValue(forKey: model.id)
    }

    func delete(_ model: LocalModel) {
        try? FileManager.default.removeItem(at: Settings.modelsDirectory.appendingPathComponent(model.id))
        refresh()
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
