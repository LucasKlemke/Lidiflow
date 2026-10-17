import AVFoundation
import Speech

/// Apple's on-device speech model (SpeechAnalyzer, macOS 26+). macOS downloads and manages its files,
/// one set per language, so there's nothing in `modelsDirectory`.
enum AppleSpeech {
    enum Failure: LocalizedError {
        case unavailable, unsupportedLanguage(String), audio, run(String)

        var errorDescription: String? {
            switch self {
            case .unavailable: return "O modelo da Apple precisa do macOS 26 ou superior"
            case .unsupportedLanguage(let code): return "O modelo da Apple não transcreve o idioma \(code)"
            case .audio: return "Não foi possível preparar o áudio"
            case .run(let message): return "Falha na transcrição da Apple: \(message)"
            }
        }
    }

    static let model = LocalModel(id: "apple-speech", name: "Apple (SpeechAnalyzer)",
                                  detail: "Do macOS, sem download extra pelo app, muito rápido",
                                  size: "gerenciado pelo macOS", speed: 97, accuracy: 84, engine: .apple)

    /// Whether this Mac runs it: macOS 26 or later on supported hardware.
    static var isSupported: Bool {
        guard #available(macOS 26, *) else { return false }
        return SpeechTranscriber.isAvailable
    }

    /// Whether macOS already has the files for `language`. (`AssetInventory.status` says only
    /// "supported" for files another app downloaded, until this one reserves them.)
    static func isInstalled(language: String) async -> Bool {
        guard #available(macOS 26, *), let locale = try? await locale(for: language) else { return false }
        return await SpeechTranscriber.installedLocales.contains { $0.identifier(.bcp47) == locale.identifier(.bcp47) }
    }

    /// Asks macOS to download the files for `language` if it doesn't have them, reporting 0–1 progress.
    static func install(language: String, progress: @escaping (Double) -> Void = { _ in }) async throws {
        guard #available(macOS 26, *) else { throw Failure.unavailable }
        let transcriber = try await transcriber(for: language)
        guard let request = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) else { return }
        let observation = request.progress.observe(\.fractionCompleted, options: [.initial]) { value, _ in
            progress(value.fractionCompleted)
        }
        defer { observation.invalidate() }
        do {
            try await request.downloadAndInstall()
        } catch {
            throw Failure.run(error.localizedDescription)
        }
    }

    /// Transcribes 16 kHz mono samples. `language` is a short code, or "auto" for the system language
    /// (the model doesn't detect it). `vocabulary` (the user's dictionary) is passed as contextual strings.
    static func transcribe(samples: [Float], language: String, vocabulary: [String] = []) async throws -> String {
        guard #available(macOS 26, *) else { throw Failure.unavailable }
        try await install(language: language)
        let transcriber = try await transcriber(for: language)
        // Keeps the model warm between dictations instead of reloading it each time.
        let analyzer = SpeechAnalyzer(modules: [transcriber],
                                      options: .init(priority: .userInitiated, modelRetention: .processLifetime))
        guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]),
              let buffer = buffer(samples, in: format) else { throw Failure.audio }
        do {
            if !vocabulary.isEmpty {
                let context = AnalysisContext()
                context.contextualStrings[.general] = vocabulary
                try await analyzer.setContext(context)
            }
            let text = Task {
                try await transcriber.results.reduce(into: "") { $0 += String($1.text.characters) }
            }
            let (input, continuation) = AsyncStream.makeStream(of: AnalyzerInput.self)
            continuation.yield(AnalyzerInput(buffer: buffer))
            continuation.finish()
            try await analyzer.start(inputSequence: input)
            try await analyzer.finalizeAndFinishThroughEndOfInput()
            return try await text.value.trimmingCharacters(in: .whitespacesAndNewlines)
        } catch {
            await analyzer.cancelAndFinishNow()
            throw Failure.run(error.localizedDescription)
        }
    }

    @available(macOS 26, *)
    private static func transcriber(for language: String) async throws -> SpeechTranscriber {
        SpeechTranscriber(locale: try await locale(for: language), preset: .transcription)
    }

    /// The model wants a region ("pt-BR", not "pt"), and `supportedLocale(equivalentTo:)` picks pt-PT for "pt".
    /// So the region comes from the user's own languages, then the Mac's region. "auto" means the
    /// first preferred language, since the model doesn't detect it.
    @available(macOS 26, *)
    private static func locale(for language: String) async throws -> Locale {
        let preferred = Locale.preferredLanguages.map(Locale.init(identifier:))
        guard let code = language == "auto" ? preferred.first?.language.languageCode?.identifier : language else {
            throw Failure.unsupportedLanguage(language)
        }
        let supported = await SpeechTranscriber.supportedLocales
        var candidates = preferred.filter { $0.language.languageCode?.identifier == code }
        if let region = Locale.current.region?.identifier { candidates.append(Locale(identifier: "\(code)-\(region)")) }
        for candidate in candidates {
            guard let region = candidate.region?.identifier else { continue }
            if let match = supported.first(where: { $0.language.languageCode?.identifier == code && $0.region?.identifier == region }) {
                return match
            }
        }
        guard let locale = await SpeechTranscriber.supportedLocale(equivalentTo: Locale(identifier: code)) else {
            throw Failure.unsupportedLanguage(language)
        }
        return locale
    }

    /// Converts the recorder's 16 kHz mono floats to the format the model wants (16 kHz mono Int16 today).
    private static func buffer(_ samples: [Float], in format: AVAudioFormat) -> AVAudioPCMBuffer? {
        guard !samples.isEmpty,
              let source = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: AudioRecorder.sampleRate,
                                         channels: 1, interleaved: false),
              let input = AVAudioPCMBuffer(pcmFormat: source, frameCapacity: AVAudioFrameCount(samples.count)),
              let converter = AVAudioConverter(from: source, to: format) else { return nil }
        input.frameLength = input.frameCapacity
        samples.withUnsafeBufferPointer { input.floatChannelData![0].update(from: $0.baseAddress!, count: samples.count) }

        let capacity = AVAudioFrameCount(Double(samples.count) * format.sampleRate / source.sampleRate) + 32
        guard let output = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else { return nil }
        var consumed = false
        var error: NSError?
        converter.convert(to: output, error: &error) { _, status in
            if consumed {
                status.pointee = .endOfStream
                return nil
            }
            consumed = true
            status.pointee = .haveData
            return input
        }
        return error == nil ? output : nil
    }
}
