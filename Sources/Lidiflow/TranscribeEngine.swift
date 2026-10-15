import CTranscribe
import Foundation

/// Runs GGUF speech models (e.g. Nemotron) in-process through transcribe.cpp, keeping the model loaded.
final class TranscribeEngine {
    enum Failure: LocalizedError {
        case modelNotFound, load(String), run(String), cancelled

        var errorDescription: String? {
            switch self {
            case .modelNotFound: return "Modelo não baixado — escolha um em Modelos"
            case .load(let message): return "Não foi possível carregar o modelo: \(message)"
            case .run(let message): return "Falha na transcrição local: \(message)"
            case .cancelled: return "Transcrição cancelada"
            }
        }
    }

    /// Drops previews still waiting in the queue once recording stops. (A pass already running
    /// can't be interrupted in most families, which is why previews get a session of their own.)
    private final class PreviewTickets {
        private let lock = NSLock()
        private var generation = 0

        func current() -> Int { lock.withLock { generation } }
        func cancelAll() { lock.withLock { generation += 1 } }
        func isValid(_ ticket: Int) -> Bool { lock.withLock { ticket == generation } }
    }

    /// Two sessions on one loaded model: previews run on their own session and queue, so the final
    /// transcription never waits behind one. Loading and freeing happen on `queue`, and touch the
    /// preview session only inside `previewQueue.sync`, after any running preview has finished.
    private let queue = DispatchQueue(label: "com.lidiflow.transcribe")
    private let previewQueue = DispatchQueue(label: "com.lidiflow.transcribe.preview")
    private let tickets = PreviewTickets()
    private var model: OpaquePointer?
    private var session: OpaquePointer?
    private var previewSession: OpaquePointer?
    private var loadedPath: String?
    /// The language codes the loaded model accepts, e.g. "pt-BR" (empty when it takes any or none).
    private var languages: [String] = []

    private static let backends: Void = { _ = transcribe_init_backends_default() }()

    /// Loads the model at `url`, unless it's already the loaded one.
    func load(_ url: URL) async throws {
        try await perform(on: queue) { try self.loadIfNeeded(url) }
    }

    func unload() {
        queue.async { self.freeModel() }
    }

    /// Transcribes 16 kHz mono samples with the model at `url`, loading it first if needed (so a failed
    /// earlier load is retried). `language` is a short code, or "auto" to let the model detect it.
    /// `vocabulary` (the user's dictionary) is passed to models that accept custom terms.
    func transcribe(samples: [Float], model url: URL, language: String, vocabulary: [String] = []) async throws -> String {
        try await perform(on: queue) {
            try self.loadIfNeeded(url)
            guard let session = self.session else { throw Failure.modelNotFound }
            return try self.run(session, samples, language: language, vocabulary: vocabulary)
        }
    }

    /// A quick pass for the live preview, on the preview session. Throws `.cancelled` when
    /// `cancelPreviews()` was called before it started, and `.modelNotFound` until a model is loaded.
    func preview(samples: [Float], language: String) async throws -> String {
        let ticket = tickets.current()
        return try await perform(on: previewQueue) {
            guard self.tickets.isValid(ticket) else { throw Failure.cancelled }
            guard let session = self.previewSession else { throw Failure.modelNotFound }
            return try self.run(session, samples, language: language, vocabulary: [])
        }
    }

    func cancelPreviews() {
        tickets.cancelAll()
    }

    // MARK: - On the queues

    private func loadIfNeeded(_ url: URL) throws {
        guard loadedPath != url.path || session == nil else { return }
        guard FileManager.default.fileExists(atPath: url.path) else { throw Failure.modelNotFound }
        freeModel()
        Self.backends
        var model: OpaquePointer?
        var status = transcribe_model_load_file(url.path, nil, &model)
        guard status == TRANSCRIBE_OK, let model else { throw Failure.load(Self.describe(status)) }
        var session: OpaquePointer?
        var previewSession: OpaquePointer?
        status = transcribe_session_init(model, nil, &session)
        if status == TRANSCRIBE_OK { status = transcribe_session_init(model, nil, &previewSession) }
        guard status == TRANSCRIBE_OK, let session, let previewSession else {
            transcribe_session_free(session)
            transcribe_session_free(previewSession)
            transcribe_model_free(model)
            throw Failure.load(Self.describe(status))
        }
        let languages = Self.languages(of: model)
        previewQueue.sync {
            self.model = model
            self.session = session
            self.previewSession = previewSession
            self.loadedPath = url.path
            self.languages = languages
        }
    }

    private func run(_ session: OpaquePointer, _ samples: [Float], language: String, vocabulary: [String]) throws -> String {
        var params = transcribe_run_params()
        transcribe_run_params_init(&params)
        let terms = transcribe_model_supports(transcribe_get_model(session), TRANSCRIBE_FEATURE_VOCABULARY) ? vocabulary : []
        let cTerms = terms.map { strdup($0) }
        defer { cTerms.forEach { free($0) } }
        let status: transcribe_status = Self.withOptionalCString(modelLanguage(for: language)) { code in
            params.language = code
            return cTerms.map { UnsafePointer($0) }.withUnsafeBufferPointer { list in
                params.vocabulary = terms.isEmpty ? nil : list.baseAddress
                params.n_vocabulary = Int32(terms.count)
                return samples.withUnsafeBufferPointer { buffer in
                    transcribe_run(session, buffer.baseAddress, Int32(buffer.count), &params)
                }
            }
        }
        guard status == TRANSCRIBE_OK else { throw Failure.run(Self.describe(status)) }
        return transcribe_full_text(session).map { String(cString: $0) }?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }

    /// Runs on `queue`. Waits for a running preview before freeing what it uses.
    private func freeModel() {
        previewQueue.sync {
            transcribe_session_free(previewSession)
            transcribe_session_free(session)
            transcribe_model_free(model)
            previewSession = nil
            session = nil
            model = nil
            loadedPath = nil
            languages = []
        }
    }

    private static func describe(_ status: transcribe_status) -> String {
        String(cString: transcribe_status_string(Int32(status.rawValue)))
    }

    /// Maps the app's short code ("pt") to the model's own ("pt-BR"); nil lets the model detect it.
    private func modelLanguage(for code: String) -> String? {
        guard code != "auto", !languages.isEmpty else { return code == "auto" ? nil : code }
        return languages.first { $0 == code } ?? languages.first { $0.hasPrefix(code + "-") }
    }

    private static func languages(of model: OpaquePointer) -> [String] {
        var capabilities = transcribe_capabilities()
        transcribe_capabilities_init(&capabilities)
        guard transcribe_model_get_capabilities(model, &capabilities) == TRANSCRIBE_OK,
              let list = capabilities.languages else { return [] }
        return (0..<Int(capabilities.n_languages)).compactMap { list[$0].map { String(cString: $0) } }
    }

    /// Runs blocking native work off the main thread, one call at a time per queue.
    private func perform<T>(on queue: DispatchQueue, _ work: @escaping () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            queue.async { continuation.resume(with: Result(catching: work)) }
        }
    }

    private static func withOptionalCString<T>(_ string: String?, _ body: (UnsafePointer<CChar>?) -> T) -> T {
        guard let string else { return body(nil) }
        return string.withCString(body)
    }
}
