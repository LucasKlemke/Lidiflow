import CryptoKit
import Foundation

/// A program or model the app downloads by itself on first use, into Application Support, checked by SHA-256.
/// Used for the speaker diarization of meetings. `state` changes only on the main actor, inside `install()`; the rest
/// is immutable, hence the unchecked Sendable.
final class Dependency: ObservableObject, @unchecked Sendable {
    enum State: Equatable {
        case missing
        case installing(Double)
        case installed
        case failed(String)
    }

    /// sherpa-onnx's speaker diarization (meetings, phase 3): the program and its two models, fetched on first use.
    static let diarizer = Dependency(
        title: "separador de vozes",
        folder: "diarization/program",
        executable: "sherpa-onnx-v1.13.8-osx-universal2-shared-no-tts/bin/sherpa-onnx-offline-speaker-diarization",
        archive: URL(string: "https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.8/sherpa-onnx-v1.13.8-osx-universal2-shared-no-tts.tar.bz2")!,
        sha256: "636b439c75031d892636dfdd2000a0233e4a30a08b030697e91f8aa389431b83"
    )
    static let segmentationModel = Dependency(
        title: "modelo de segmentação",
        folder: "diarization/segmentation",
        executable: "sherpa-onnx-pyannote-segmentation-3-0/model.onnx",
        archive: URL(string: "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2")!,
        sha256: "24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488"
    )
    static let voiceModel = Dependency(
        title: "modelo de voz",
        folder: "diarization/voice",
        executable: "campplus.onnx",
        archive: URL(string: "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx")!,
        sha256: "357a834f702b80161e5b981182c038e18553c1f2ca752ed6cec2052365d4129b",
        isArchive: false
    )

    let title: String
    private let folder: String
    /// The file that marks the install as done, relative to the folder (a program, or a model).
    private let executable: String
    private let archive: URL
    private let sha256: String?
    /// False for a single file (a model) saved as is, rather than an archive to unpack.
    private let isArchive: Bool

    @Published private(set) var state: State

    private init(title: String, folder: String, executable: String, archive: URL, sha256: String?, isArchive: Bool = true) {
        self.title = title
        self.folder = folder
        self.executable = executable
        self.archive = archive
        self.sha256 = sha256
        self.isArchive = isArchive
        let present = Self.locate(folder: folder, executable: executable) != nil
        _state = Published(initialValue: present ? .installed : .missing)
    }

    private var directory: URL {
        Settings.supportDirectory.appendingPathComponent(folder, isDirectory: true)
    }

    /// The installed file, once it is there.
    var binaryPath: String? {
        Self.locate(folder: folder, executable: executable)
    }

    private static func locate(folder: String, executable: String) -> String? {
        let installed = Settings.supportDirectory.appendingPathComponent(folder).appendingPathComponent(executable).path
        return FileManager.default.fileExists(atPath: installed) ? installed : nil
    }

    /// Downloads and unpacks the tool. Safe to call repeatedly: it returns at once if the tool is there or on its way.
    @MainActor
    func install() async {
        if binaryPath != nil {
            state = .installed
            return
        }
        if case .installing = state { return }
        state = .installing(0)

        let fileManager = FileManager.default
        let staging = Settings.supportDirectory.appendingPathComponent(".\(folder.replacingOccurrences(of: "/", with: "-"))-install", isDirectory: true)
        do {
            let progress = DownloadProgress { [weak self] fraction in
                Task { @MainActor in
                    if case .installing = self?.state { self?.state = .installing(fraction) }
                }
            }
            let (file, response) = try await URLSession.shared.download(from: archive, delegate: progress)
            defer { try? fileManager.removeItem(at: file) }
            guard (response as? HTTPURLResponse)?.statusCode == 200 else {
                throw InstallError("o download respondeu \((response as? HTTPURLResponse)?.statusCode ?? 0)")
            }
            if let sha256 {
                let digest = SHA256.hash(data: try Data(contentsOf: file, options: .mappedIfSafe))
                guard digest.map({ String(format: "%02x", $0) }).joined() == sha256 else {
                    throw InstallError("o pacote baixado não é o esperado e foi descartado")
                }
            }

            // Unpack next to the final folder and swap it in only when complete, so a failed install leaves nothing half-done.
            try? fileManager.removeItem(at: staging)
            try fileManager.createDirectory(at: staging, withIntermediateDirectories: true)
            if isArchive {
                try await Self.untar(file, into: staging)
            } else {
                try fileManager.copyItem(at: file, to: staging.appendingPathComponent(executable))
            }
            guard fileManager.fileExists(atPath: staging.appendingPathComponent(executable).path) else {
                throw InstallError("o pacote não tem o \(executable)")
            }
            try? fileManager.removeItem(at: directory)
            try fileManager.createDirectory(at: directory.deletingLastPathComponent(), withIntermediateDirectories: true)
            try fileManager.moveItem(at: staging, to: directory)
            state = .installed
        } catch {
            try? fileManager.removeItem(at: staging)
            state = .failed("Não foi possível instalar o \(title): \(error.localizedDescription)")
        }
    }

    private static func untar(_ archive: URL, into directory: URL) async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
        // -x without -z: tar detects gzip and bzip2 by itself (whisper and Ollama ship .tgz, sherpa-onnx .tar.bz2).
        process.arguments = ["-xf", archive.path, "-C", directory.path]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            process.terminationHandler = { process in
                if process.terminationStatus == 0 {
                    continuation.resume()
                } else {
                    continuation.resume(throwing: InstallError("não foi possível descompactar o pacote"))
                }
            }
            do { try process.run() } catch { continuation.resume(throwing: error) }
        }
    }
}

private struct InstallError: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}

/// Reports a download's progress by watching the task the async download call creates.
private final class DownloadProgress: NSObject, URLSessionTaskDelegate {
    private let onProgress: (Double) -> Void
    private var observation: NSKeyValueObservation?

    init(_ onProgress: @escaping (Double) -> Void) {
        self.onProgress = onProgress
    }

    func urlSession(_ session: URLSession, didCreateTask task: URLSessionTask) {
        observation = task.progress.observe(\.fractionCompleted) { [onProgress] progress, _ in
            onProgress(progress.fractionCompleted)
        }
    }
}
