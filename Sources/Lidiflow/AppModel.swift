import AppKit
import AVFoundation

struct Dictation: Codable, Identifiable, Hashable {
    var id = UUID()
    let date: Date
    let text: String
    let duration: TimeInterval

    var words: Int { text.split(whereSeparator: \.isWhitespace).count }
}

/// Shared state between the dictation pipeline and the main window.
final class AppModel: ObservableObject {
    @Published var status = "Iniciando…"
    @Published var isReady = false
    @Published private(set) var history: [Dictation] = []
    @Published private(set) var accessibilityGranted = AXIsProcessTrusted()
    @Published private(set) var microphoneGranted = false

    @Published var language = Settings.language {
        didSet { Settings.language = language }
    }
    @Published var shortcuts = Settings.shortcuts {
        didSet {
            Settings.shortcuts = shortcuts
            onShortcutsChange(shortcuts)
        }
    }
    @Published var appearance = Settings.appearance {
        didSet {
            Settings.appearance = appearance
            Self.apply(appearance)
        }
    }
    @Published var soundsEnabled = Settings.soundsEnabled {
        didSet { Settings.soundsEnabled = soundsEnabled }
    }
    @Published var dictionary: [String] = [] {
        didSet { saveVocabulary() }
    }
    @Published var replacements: [Replacement] = [] {
        didSet { saveVocabulary() }
    }
    @Published var snippets: [Snippet] = [] {
        didSet { saveVocabulary() }
    }
    @Published var polishEnabled = Settings.polishEnabled {
        didSet { Settings.polishEnabled = polishEnabled }
    }
    @Published var transformsEnabled = Settings.transformsEnabled {
        didSet { Settings.transformsEnabled = transformsEnabled }
    }
    @Published var autoTransformEnabled = Settings.autoTransformEnabled {
        didSet { Settings.autoTransformEnabled = autoTransformEnabled }
    }
    @Published var autoTransformID = Settings.autoTransformID {
        didSet { Settings.autoTransformID = autoTransformID }
    }

    /// The transform applied after every dictation, when auto-apply is on.
    var autoTransform: Transform? {
        guard transformsEnabled, autoTransformEnabled else { return nil }
        return transforms.first { $0.id == autoTransformID } ?? transforms.first
    }

    @Published var transforms: [Transform] = Transform.defaults {
        didSet { saveTransforms() }
    }

    // MARK: Models

    @Published var transcriptionEngine = Settings.transcriptionEngine {
        didSet {
            Settings.transcriptionEngine = transcriptionEngine
            onEngineChange()
        }
    }
    @Published var localModel = Settings.localModel {
        didSet {
            Settings.localModel = localModel
            onEngineChange()
        }
    }
    @Published var transcriptionProvider = Settings.transcriptionProvider {
        didSet {
            Settings.transcriptionProvider = transcriptionProvider
            transcriptionModel = Settings.transcriptionModel(for: transcriptionProvider)
            onEngineChange()
        }
    }
    @Published var transcriptionModel = Settings.transcriptionModel(for: Settings.transcriptionProvider) {
        didSet { Settings.setTranscriptionModel(transcriptionModel, for: transcriptionProvider) }
    }
    @Published var llmProvider = Settings.llmProvider {
        didSet {
            Settings.llmProvider = llmProvider
            if llmProvider != .ollama { Settings.lastCloudProvider = llmProvider }
            llmModel = Settings.model(for: llmProvider)
        }
    }

    /// Whether the AI runs on this Mac (Ollama) or on a cloud account.
    var aiRunsLocally: Bool {
        get { llmProvider == .ollama }
        set { llmProvider = newValue ? .ollama : Settings.lastCloudProvider }
    }
    @Published var llmModel = Settings.model(for: Settings.llmProvider) {
        didSet { Settings.setModel(llmModel, for: llmProvider) }
    }
    @Published var ollamaBaseURL = Settings.ollamaBaseURL {
        didSet { Settings.ollamaBaseURL = ollamaBaseURL }
    }
    /// Set while the user records a shortcut, so the global shortcuts stay quiet.
    @Published var isRecordingShortcut = false
    /// Providers that have an API key saved in the Keychain.
    @Published private(set) var savedKeys = Set(LLMProvider.allCases.filter { Keychain.key(for: $0) != nil })

    let models = ModelManager()
    let ollama = OllamaManager()

    var onShortcutsChange: ([ShortcutAction: [KeyCombo]]) -> Void = { _ in }
    /// Called when the transcription engine, local model or API key changes.
    var onEngineChange: () -> Void = {}

    private let historyURL = Settings.supportDirectory.appendingPathComponent("history.json")
    private let vocabularyURL = Settings.supportDirectory.appendingPathComponent("vocabulary.json")
    private let transformsURL = Settings.supportDirectory.appendingPathComponent("transforms.json")

    private struct Vocabulary: Codable {
        var dictionary: [String] = []
        var replacements: [Replacement] = []
        var snippets: [Snippet] = []
    }

    init() {
        if let data = try? Data(contentsOf: historyURL),
           let saved = try? JSONDecoder().decode([Dictation].self, from: data) {
            history = saved
        }
        if let data = try? Data(contentsOf: vocabularyURL),
           let saved = try? JSONDecoder().decode(Vocabulary.self, from: data) {
            // Property observers don't run during init, so this doesn't re-save.
            dictionary = saved.dictionary
            replacements = saved.replacements
            snippets = saved.snippets
        }
        if let data = try? Data(contentsOf: transformsURL),
           let saved = try? JSONDecoder().decode([Transform].self, from: data) {
            transforms = saved
        }
        refreshPermissions()
    }

    /// Light, dark, or nil to follow the system. The window, overlay and menus all pick it up.
    static func apply(_ appearance: Appearance) {
        switch appearance {
        case .system: NSApp.appearance = nil
        case .light: NSApp.appearance = NSAppearance(named: .aqua)
        case .dark: NSApp.appearance = NSAppearance(named: .darkAqua)
        }
    }

    // MARK: - History

    func add(_ dictation: Dictation) {
        history.insert(dictation, at: 0)
        saveHistory()
    }

    func delete(_ dictation: Dictation) {
        history.removeAll { $0.id == dictation.id }
        saveHistory()
    }

    func clearHistory() {
        history.removeAll()
        saveHistory()
    }

    private func saveHistory() {
        try? JSONEncoder().encode(history).write(to: historyURL, options: .atomic)
    }

    private func saveVocabulary() {
        let vocabulary = Vocabulary(dictionary: dictionary, replacements: replacements, snippets: snippets)
        try? JSONEncoder().encode(vocabulary).write(to: vocabularyURL, options: .atomic)
    }

    /// The transform's own shortcut, or base key + its position for the first nine.
    func shortcut(for index: Int) -> KeyCombo? {
        guard transforms.indices.contains(index) else { return nil }
        return transforms[index].shortcut ?? defaultShortcut(for: index)
    }

    /// Dictation key + position, when dictation is held with modifiers alone (e.g. Fn + 1).
    func defaultShortcut(for index: Int) -> KeyCombo? {
        guard HotkeyMonitor.digits.indices.contains(index),
              let base = shortcuts[.dictate]?.first(where: \.isModifierOnly) else { return nil }
        return KeyCombo(keyCode: HotkeyMonitor.digits[index], flags: base.flags, keyLabel: "\(index + 1)")
    }

    /// Key caps of an action's first shortcut, for hints around the app.
    func keys(for action: ShortcutAction) -> [String] {
        shortcuts[action]?.first?.keys ?? []
    }

    /// The dictation shortcut as text, e.g. "Fn" or "⌃⌥ Espaço".
    var dictateLabel: String {
        let keys = keys(for: .dictate)
        return keys.isEmpty ? "o atalho" : keys.joined(separator: " ")
    }

    /// The shortcut slot being edited: an action's, or a transform's (nil index for a new one).
    enum ShortcutSlot {
        case action(ShortcutAction)
        case transform(Int?)
    }

    /// Why a combo can't be used in `slot`, if it can't.
    func conflict(for combo: KeyCombo, in slot: ShortcutSlot) -> String? {
        var editedAction: ShortcutAction?
        var editedTransform: Int?
        switch slot {
        case .action(let action):
            editedAction = action
        case .transform(let index):
            editedTransform = index
            if combo.isModifierOnly { return "Transforms precisam de uma tecla ou botão do mouse" }
        }
        if !combo.isUsable(allowPlainKeys: editedAction == .cancel) {
            return "Use um modificador (⌘ ⌃ ⌥ ⇧ Fn), uma tecla F ou um botão do mouse"
        }
        for (action, combos) in shortcuts where combos.contains(combo) {
            return action == editedAction ? "Esse atalho já está nesta ação" : "Já usado para \(action.title.lowercased())"
        }
        for index in transforms.indices where index != editedTransform && shortcut(for: index) == combo {
            return "Já usado pelo transform \u{201C}\(transforms[index].name)\u{201D}"
        }
        return nil
    }

    private func saveTransforms() {
        try? JSONEncoder().encode(transforms).write(to: transformsURL, options: .atomic)
    }

    /// Deletes a downloaded transcription model. If it was in use, switches to another downloaded
    /// one, or reloads the engine so the status says no model is available.
    func uninstall(_ item: LocalModel) {
        models.delete(item)
        guard localModel == item.id else { return }
        if let other = ModelManager.catalog.first(where: { models.installed.contains($0.id) }) {
            localModel = other.id
        } else {
            onEngineChange()
        }
    }

    /// Deletes an Ollama model; if it was in use, switches to another installed one.
    @MainActor
    func uninstallOllama(_ id: String) async {
        let inUse = llmModel == id || llmModel + ":latest" == id
        await ollama.delete(id)
        if inUse, llmProvider == .ollama, let other = ollama.installed.first { llmModel = other }
    }

    /// Returns false when the Keychain refused the change (e.g. access denied); nothing is changed then.
    @discardableResult
    func saveKey(_ key: String, for provider: LLMProvider) -> Bool {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard Keychain.setKey(trimmed.isEmpty ? nil : trimmed, for: provider) else { return false }
        if trimmed.isEmpty { savedKeys.remove(provider) } else { savedKeys.insert(provider) }
        if provider == transcriptionProvider { onEngineChange() }
        return true
    }

    var totalWords: Int { history.reduce(0) { $0 + $1.words } }

    /// Consecutive days with at least one dictation, ending today or yesterday.
    var streakDays: Int {
        let calendar = Calendar.current
        let days = Set(history.map { calendar.startOfDay(for: $0.date) })
        var day = calendar.startOfDay(for: Date())
        if !days.contains(day) { day = calendar.date(byAdding: .day, value: -1, to: day)! }
        var streak = 0
        while days.contains(day) {
            streak += 1
            day = calendar.date(byAdding: .day, value: -1, to: day)!
        }
        return streak
    }

    var wordsPerMinute: Int {
        let minutes = history.reduce(0) { $0 + $1.duration } / 60
        return minutes > 0 ? Int(Double(totalWords) / minutes) : 0
    }

    // MARK: - Permissions

    func refreshPermissions() {
        let accessibility = AXIsProcessTrusted()
        let microphone = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        if accessibility != accessibilityGranted { accessibilityGranted = accessibility }
        if microphone != microphoneGranted { microphoneGranted = microphone }
    }

    func requestMicrophone() {
        AVCaptureDevice.requestAccess(for: .audio) { _ in
            DispatchQueue.main.async { self.refreshPermissions() }
        }
    }

    func openAccessibilitySettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }

    func openMicrophoneSettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!)
    }
}
