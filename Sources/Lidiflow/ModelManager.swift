import Foundation

struct WhisperModel: Identifiable, Hashable {
    let id: String
    let name: String
    let detail: String
    let size: String

    var url: URL { URL(string: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/\(id)")! }
}

/// Downloads and tracks whisper.cpp models in Application Support.
final class ModelManager: NSObject, ObservableObject, URLSessionDownloadDelegate {
    static let catalog = [
        WhisperModel(id: "ggml-base.bin", name: "Base", detail: "Muito rápido, menos preciso", size: "142 MB"),
        WhisperModel(id: "ggml-small.bin", name: "Small", detail: "Rápido, bom para frases curtas", size: "466 MB"),
        WhisperModel(id: "ggml-large-v3-turbo-q5_0.bin", name: "Large v3 Turbo (comprimido)", detail: "Recomendado: ótima precisão e rápido", size: "547 MB"),
        WhisperModel(id: "ggml-medium.bin", name: "Medium", detail: "Preciso, mais lento", size: "1,5 GB"),
        WhisperModel(id: "ggml-large-v3-turbo.bin", name: "Large v3 Turbo", detail: "Precisão máxima do Turbo", size: "1,6 GB"),
        WhisperModel(id: "ggml-large-v3.bin", name: "Large v3", detail: "O mais preciso, o mais lento", size: "3,1 GB"),
    ]

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

    func download(_ model: WhisperModel) {
        guard tasks[model.id] == nil else { return }
        lastError = nil
        let task = session.downloadTask(with: model.url)
        task.taskDescription = model.id
        tasks[model.id] = task
        progress[model.id] = 0
        task.resume()
    }

    func cancel(_ model: WhisperModel) {
        tasks.removeValue(forKey: model.id)?.cancel()
        progress.removeValue(forKey: model.id)
    }

    func delete(_ model: WhisperModel) {
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
