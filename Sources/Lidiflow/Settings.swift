import Foundation

/// Where speech becomes text. `cloud` keeps the raw value "openAI" from earlier versions.
enum TranscriptionEngine: String, CaseIterable {
    case local
    case cloud = "openAI"
}

enum Appearance: String, CaseIterable, Identifiable {
    case system, light, dark

    var id: Self { self }

    var title: String {
        switch self {
        case .system: return "Sistema"
        case .light: return "Claro"
        case .dark: return "Escuro"
        }
    }
}

/// User preferences persisted in UserDefaults. API keys live in `Keychain`.
enum Settings {
    private static let defaults = UserDefaults.standard

    static var language: String {
        get { defaults.string(forKey: "language") ?? "pt" }
        set { defaults.set(newValue, forKey: "language") }
    }

    /// Up to two shortcuts per action.
    static var shortcuts: [ShortcutAction: [KeyCombo]] {
        get {
            guard let data = defaults.data(forKey: "shortcuts"),
                  let saved = try? JSONDecoder().decode([String: [KeyCombo]].self, from: data) else {
                return ShortcutAction.defaults
            }
            var result: [ShortcutAction: [KeyCombo]] = [:]
            for (key, combos) in saved {
                if let action = ShortcutAction(rawValue: key) { result[action] = combos }
            }
            return result
        }
        set {
            let raw = Dictionary(uniqueKeysWithValues: newValue.map { ($0.key.rawValue, $0.value) })
            defaults.set(try? JSONEncoder().encode(raw), forKey: "shortcuts")
        }
    }

    static var appearance: Appearance {
        get { Appearance(rawValue: defaults.string(forKey: "appearance") ?? "") ?? .system }
        set { defaults.set(newValue.rawValue, forKey: "appearance") }
    }

    /// Core Audio UID of the chosen microphone; nil uses the system default.
    static var microphone: String? {
        get { defaults.string(forKey: "microphone") }
        set { defaults.set(newValue, forKey: "microphone") }
    }

    /// Set once the first-launch setup is finished.
    static var onboardingDone: Bool {
        get { defaults.bool(forKey: "onboardingDone") }
        set { defaults.set(newValue, forKey: "onboardingDone") }
    }

    /// The dictation bar at the bottom of the screen while idle; it always appears while dictating.
    static var showsDictationBar: Bool {
        get { defaults.object(forKey: "showsDictationBar") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "showsDictationBar") }
    }

    /// Where the dictation bar docks; changed by dragging it or from its menu.
    static var dictationBarEdge: DockEdge {
        get { DockEdge(rawValue: defaults.string(forKey: "dictationBarEdge") ?? "") ?? .bottom }
        set { defaults.set(newValue.rawValue, forKey: "dictationBarEdge") }
    }

    static var soundsEnabled: Bool {
        get { defaults.object(forKey: "soundsEnabled") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "soundsEnabled") }
    }

    /// Master switch for transform shortcuts.
    static var transformsEnabled: Bool {
        get { defaults.object(forKey: "transformsEnabled") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "transformsEnabled") }
    }

    /// A transform applied to every dictation, if enabled.
    static var autoTransformEnabled: Bool {
        get { defaults.bool(forKey: "autoTransformEnabled") }
        set { defaults.set(newValue, forKey: "autoTransformEnabled") }
    }

    static var autoTransformID: UUID? {
        get { defaults.string(forKey: "autoTransformID").flatMap(UUID.init(uuidString:)) }
        set { defaults.set(newValue?.uuidString, forKey: "autoTransformID") }
    }

    /// The user's own instructions, added to every AI pass that writes text (polish, command mode, transforms).
    static var aiInstructions: String {
        get { defaults.string(forKey: "aiInstructions") ?? "" }
        set { defaults.set(newValue, forKey: "aiInstructions") }
    }

    static var polishEnabled: Bool {
        get { defaults.bool(forKey: "polishEnabled") }
        set { defaults.set(newValue, forKey: "polishEnabled") }
    }

    // MARK: - Transcription

    static var transcriptionEngine: TranscriptionEngine {
        get { TranscriptionEngine(rawValue: defaults.string(forKey: "transcriptionEngine") ?? "") ?? .local }
        set { defaults.set(newValue.rawValue, forKey: "transcriptionEngine") }
    }

    /// Id of the local model: a file name in `modelsDirectory`, or Apple's. An old whisper.cpp
    /// model reads as its GGUF build.
    static var localModel: String {
        get {
            let saved = defaults.string(forKey: "localModel") ?? ModelManager.recommended.id
            return ModelManager.legacyWhisper[saved] ?? saved
        }
        set { defaults.set(newValue, forKey: "localModel") }
    }

    static var transcriptionProvider: LLMProvider {
        get { LLMProvider(rawValue: defaults.string(forKey: "transcriptionProvider") ?? "") ?? .openAI }
        set { defaults.set(newValue.rawValue, forKey: "transcriptionProvider") }
    }

    static func transcriptionModel(for provider: LLMProvider) -> String {
        let key = provider == .openAI ? "openAITranscriptionModel" : "transcriptionModel.\(provider.rawValue)"
        return defaults.string(forKey: key) ?? CloudTranscriber.models(for: provider)[0]
    }

    static func setTranscriptionModel(_ model: String, for provider: LLMProvider) {
        let key = provider == .openAI ? "openAITranscriptionModel" : "transcriptionModel.\(provider.rawValue)"
        defaults.set(model, forKey: key)
    }

    /// The cloud AI provider to return to when switching from local back to cloud.
    static var lastCloudProvider: LLMProvider {
        get { LLMProvider(rawValue: defaults.string(forKey: "lastCloudProvider") ?? "") ?? .openAI }
        set { defaults.set(newValue.rawValue, forKey: "lastCloudProvider") }
    }

    // MARK: - LLM (polish, command mode, transforms)

    static var llmProvider: LLMProvider {
        get { LLMProvider(rawValue: defaults.string(forKey: "llmProvider") ?? "") ?? .ollama }
        set { defaults.set(newValue.rawValue, forKey: "llmProvider") }
    }

    static var ollamaBaseURL: String {
        get { defaults.string(forKey: "polishBaseURL") ?? "http://localhost:11434/v1" }
        set { defaults.set(newValue, forKey: "polishBaseURL") }
    }

    /// Chosen model per provider; earlier versions stored Ollama's and OpenAI's under these keys.
    static func model(for provider: LLMProvider) -> String {
        defaults.string(forKey: modelKey(for: provider)) ?? provider.suggestedModels.first ?? ""
    }

    static func setModel(_ model: String, for provider: LLMProvider) {
        defaults.set(model, forKey: modelKey(for: provider))
    }

    private static func modelKey(for provider: LLMProvider) -> String {
        switch provider {
        case .ollama: return "polishModel"
        case .openAI: return "openAIChatModel"
        default: return "llmModel.\(provider.rawValue)"
        }
    }

    // MARK: - Paths

    static var supportDirectory: URL {
        let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Lidiflow", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    static var modelsDirectory: URL {
        let url = supportDirectory.appendingPathComponent("models", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    static var modelPath: URL {
        modelsDirectory.appendingPathComponent(localModel)
    }
}
