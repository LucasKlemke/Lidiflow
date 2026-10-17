import AppKit
import AVFoundation

/// Menu bar app that wires the global shortcuts to recording, transcription and insertion.
final class AppController: NSObject, NSApplicationDelegate {
    private enum Mode { case dictation, command }

    private struct Session {
        var mode: Mode
        let startedAt = Date()
        var handsFree = false
        var selection: Task<String?, Never>?
    }

    private let model = AppModel()
    private let transcribeEngine = TranscribeEngine()
    private let recorder = AudioRecorder()
    private let overlay = OverlayWindow()
    private lazy var mainWindow = MainWindowController(model: model)
    private lazy var onboarding = OnboardingWindowController(model: model) { [weak self] in self?.mainWindow.show() }
    private let hotkey = HotkeyMonitor()

    private var statusItem: NSStatusItem!
    private let statusLine = NSMenuItem(title: "Iniciando…", action: nil, keyEquivalent: "")
    private var session: Session?
    private var previewTask: Task<Void, Never>?
    private var permissionTimer: Timer?

    private static let minimumDuration: TimeInterval = 0.35

    func applicationDidFinishLaunching(_ notification: Notification) {
        AppModel.apply(Settings.appearance)
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "waveform", accessibilityDescription: "Lidiflow")
        buildMenu()
        buildMainMenu()
        // First launch: the setup window asks for each permission in turn, instead of every prompt at once.
        let onboarding = OnboardingWindowController.isNeeded
        if onboarding { self.onboarding.show() } else { mainWindow.show() }

        recorder.onLevel = { [weak self] level in
            DispatchQueue.main.async { self?.overlay.state.levels.pushLevel(level) }
        }
        hotkey.onAction = { [weak self] action in self?.handle(action) }
        hotkey.isRecording = { [weak self] in self?.session != nil }
        hotkey.isPaused = { [weak self] in self?.model.isRecordingShortcut == true }
        hotkey.transformIndex = { [weak self] code, flags, baseDown in
            guard let model = self?.model, model.transformsEnabled else { return nil }
            return model.transforms.indices.first { index in
                // Positional defaults only apply while the base key is held.
                guard model.transforms[index].shortcut != nil || baseDown,
                      let combo = model.shortcut(for: index) else { return false }
                return combo.matches(keyCode: code, flags: flags)
            }
        }
        hotkey.isHandsFree = { [weak self] in self?.session?.handsFree == true }
        hotkey.shortcuts = model.shortcuts
        model.onShortcutsChange = { [weak self] shortcuts in
            self?.hotkey.shortcuts = shortcuts
            if self?.model.isReady == true { self?.setReady() }
        }
        model.onEngineChange = { [weak self] in
            Task { @MainActor in await self?.prepareEngine() }
        }

        if !onboarding { model.requestMicrophone() }
        startHotkeyWhenTrusted(prompt: !onboarding)
        Task { @MainActor in await prepareEngine() }
    }

    /// Starts or stops the local server to match the selected transcription engine.
    @MainActor
    private func prepareEngine() async {
        model.isReady = false
        switch Settings.transcriptionEngine {
        case .cloud:
            transcribeEngine.unload()
            let provider = Settings.transcriptionProvider
            guard Keychain.key(for: provider) != nil else {
                setStatus(CloudTranscriber.Failure.missingKey(provider).localizedDescription)
                return
            }
        case .local:
            setStatus("Carregando modelo…")
            do {
                switch ModelManager.selected?.engine {
                case .apple:
                    transcribeEngine.unload()
                    // Downloads the current language's files the first time, if macOS doesn't have them.
                    try await AppleSpeech.install(language: Settings.language)
                    model.models.refresh()
                default:
                    try await transcribeEngine.load(Settings.modelPath)
                }
            } catch {
                // Don't keep a model loaded that's no longer the selected, downloaded one.
                transcribeEngine.unload()
                setStatus(error.localizedDescription)
                return
            }
        }
        if AXIsProcessTrusted() { setReady() } else { setStatus("Permita o Lidiflow em Acessibilidade") }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        mainWindow.show()
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        transcribeEngine.unloadNow()
        model.ollama.stop()
    }

    // MARK: - Permissions

    private func startHotkeyWhenTrusted(prompt: Bool) {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: prompt] as CFDictionary
        if AXIsProcessTrustedWithOptions(options), hotkey.start() { return }

        permissionTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] timer in
            guard let self, AXIsProcessTrusted(), self.hotkey.start() else { return }
            timer.invalidate()
            self.model.refreshPermissions()
            if self.statusLine.title.hasPrefix("Permita") { Task { @MainActor in await self.prepareEngine() } }
        }
    }

    // MARK: - Shortcuts

    private func handle(_ action: HotkeyMonitor.Action) {
        switch action {
        case .holdStart(.command):
            begin(.command)
        case .holdStart:
            begin(.dictation)
        case .switchToCommand:
            switchToCommand()
        case .holdEnd:
            if session?.handsFree == false { finish() }
        case .handsFree:
            toggleHandsFree()
        case .stop:
            finish()
        case .cancel:
            cancel()
        case .discard:
            discardRecording()
        case .pasteLast:
            if let last = model.history.first { TextInserter.insert(last.text) }
        case .copyLast:
            copyLast()
        case .transform(let index):
            if session?.handsFree == false { discardRecording() }
            runTransform(index)
        }
    }

    /// Starts a hands-free dictation, locks the one being held, or stops a hands-free one.
    private func toggleHandsFree() {
        guard let current = session else {
            begin(.dictation)
            guard session != nil else { return }
            session?.handsFree = true
            overlay.show(.handsFree)
            return
        }
        if current.handsFree {
            finish()
            return
        }
        session?.handsFree = true
        overlay.show(current.mode == .command ? .command : .handsFree)
        play("Tink")
    }

    // MARK: - Dictation

    private func begin(_ mode: Mode) {
        guard session == nil else { return }
        do {
            try recorder.start()
        } catch {
            flashError("Microfone indisponível")
            return
        }
        session = Session(mode: mode)
        if mode == .command { captureSelection() }
        overlay.show(mode == .command ? .command : .listening)
        play("Pop")
        startPreview()
    }

    /// Re-transcribes the last seconds while recording so the pill can show the words as they come.
    /// Local engines only: with a cloud key every preview would be a billed request. Skipped for the
    /// slowest models, whose passes would compete with the final transcription for the GPU.
    private func startPreview() {
        stopPreview()
        guard Settings.transcriptionEngine == .local, let local = ModelManager.selected,
              local.speed >= 35 else { return }
        previewTask = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(800))
            while !Task.isCancelled {
                guard let self, self.session != nil else { return }
                // Only the tail is visible, so a short window is plenty and keeps each pass fast.
                let samples = self.recorder.tail(seconds: 10)
                if samples.count >= Int(AudioRecorder.sampleRate) / 2, AudioRecorder.hasVoice(samples),
                   let text = try? await self.transcribe(samples, preview: true), !Task.isCancelled, !text.isEmpty {
                    self.overlay.state.live = text
                }
                try? await Task.sleep(for: .milliseconds(600))
            }
        }
    }

    private func stopPreview() {
        previewTask?.cancel()
        previewTask = nil
        transcribeEngine.cancelPreviews()
        overlay.state.live = ""
    }

    private func switchToCommand() {
        guard session != nil else { return }
        session?.mode = .command
        captureSelection()
        overlay.show(.command)
    }

    private func captureSelection() {
        session?.selection = Task { @MainActor in await TextInserter.selectedText() }
    }

    private func cancel() {
        guard session != nil else { return }
        discardRecording()
        play("Bottle")
    }

    private func discardRecording() {
        guard session != nil else { return }
        session = nil
        stopPreview()
        _ = recorder.stop()
        overlay.show(.hidden)
    }

    /// Applies a saved transform to the focused app's selected text.
    private func runTransform(_ index: Int) {
        guard model.transforms.indices.contains(index) else {
            flashError("Nenhum transform no \(index + 1)")
            return
        }
        let transform = model.transforms[index]
        Task { @MainActor in
            guard let text = await TextInserter.selectedText() else {
                flashError("Selecione um texto primeiro")
                return
            }
            overlay.show(.transforming(transform.name))
            do {
                let result = try await LLM.transform(text, with: transform.prompt)
                overlay.show(.hidden)
                TextInserter.insert(result)
            } catch {
                flashError(Self.message(for: error))
            }
        }
    }

    private func finish() {
        guard let session else { return }
        self.session = nil
        stopPreview()
        let samples = recorder.stop()
        let duration = Date().timeIntervalSince(session.startedAt)

        // Silence (a muted or wrong microphone) makes Whisper invent YouTube outros, so skip it.
        guard duration >= Self.minimumDuration, AudioRecorder.hasVoice(samples) else {
            overlay.show(.hidden)
            return
        }
        play("Morse")
        overlay.show(.transcribing)

        Task { @MainActor in
            do {
                let spoken = try await transcribe(samples)
                guard !spoken.isEmpty else {
                    overlay.show(.hidden)
                    return
                }

                let text: String
                switch session.mode {
                case .dictation:
                    text = try await processDictation(spoken)
                case .command:
                    overlay.show(.thinking)
                    text = try await LLM.command(spoken, selection: await session.selection?.value)
                }

                model.add(Dictation(date: Date(), text: text, duration: duration))
                overlay.show(.hidden)
                TextInserter.insert(text)
            } catch {
                flashError(Self.message(for: error))
                NSLog("Lidiflow: \(error.localizedDescription)")
            }
        }
    }

    /// The single path from samples to text. A `preview` pass is cancellable and skips the dictionary hints.
    private func transcribe(_ samples: [Float], preview: Bool = false) async throws -> String {
        TextProcessor.removeHallucinations(try await rawTranscription(samples, preview: preview))
    }

    private func rawTranscription(_ samples: [Float], preview: Bool) async throws -> String {
        let language = Settings.language
        let prompt = preview ? nil : TextProcessor.whisperPrompt(for: model.dictionary)
        switch Settings.transcriptionEngine {
        case .local where ModelManager.selected?.engine == .apple:
            return try await AppleSpeech.transcribe(samples: samples, language: language,
                                                    vocabulary: preview ? [] : model.dictionary)
        case .local:
            return preview
                ? try await transcribeEngine.preview(samples: samples, language: language)
                : try await transcribeEngine.transcribe(samples: samples, model: Settings.modelPath,
                                                        language: language, vocabulary: model.dictionary)
        case .cloud:
            return try await CloudTranscriber.transcribe(wav: AudioRecorder.wavData(from: samples), language: language, prompt: prompt)
        }
    }

    private static func message(for error: Error) -> String {
        switch error {
        case let error as LLM.Failure: return error.localizedDescription
        case let error as CloudTranscriber.Failure: return error.localizedDescription
        case let error as TranscribeEngine.Failure: return error.localizedDescription
        case let error as AppleSpeech.Failure: return error.localizedDescription
        default: return "Falha na transcrição"
        }
    }

    @MainActor
    private func processDictation(_ spoken: String) async throws -> String {
        var text = spoken
        if Settings.polishEnabled {
            overlay.show(.polishing)
            text = (try? await LLM.polish(text)) ?? text
        }
        if let transform = model.autoTransform {
            overlay.show(.transforming(transform.name))
            text = (try? await LLM.transform(text, with: transform.prompt)) ?? text
        }
        return TextProcessor.apply(text, replacements: model.replacements, snippets: model.snippets)
    }

    private func flashError(_ message: String) {
        overlay.show(.error(message))
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { [weak self] in
            if case .error = self?.overlay.state.phase { self?.overlay.show(.hidden) }
        }
    }

    private func play(_ sound: String) {
        guard Settings.soundsEnabled else { return }
        NSSound(named: sound)?.play()
    }

    // MARK: - Status & menu

    private func setReady() {
        model.isReady = true
        setStatus("Pronto — segure \(model.dictateLabel) para ditar")
    }

    private func setStatus(_ text: String) {
        statusLine.title = text
        model.status = text
    }

    private func buildMenu() {
        let menu = NSMenu()
        statusLine.isEnabled = false
        menu.addItem(statusLine)
        menu.addItem(.separator())

        let open = NSMenuItem(title: "Abrir Lidiflow", action: #selector(openMainWindow), keyEquivalent: "o")
        open.target = self
        menu.addItem(open)

        let copyLast = NSMenuItem(title: "Copiar última transcrição", action: #selector(copyLast), keyEquivalent: "c")
        copyLast.target = self
        menu.addItem(copyLast)

        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Sair do Lidiflow", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        statusItem.menu = menu
    }

    /// Standard key equivalents (⌘C, ⌘V, ⌘A, ⌘Z, ⌘W…) are dispatched through the main menu,
    /// so without one text fields in the window ignore them.
    private func buildMainMenu() {
        let main = NSMenu()

        let appMenu = NSMenu()
        appMenu.addItem(NSMenuItem(title: "Ocultar Lidiflow", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h"))
        appMenu.addItem(.separator())
        appMenu.addItem(NSMenuItem(title: "Sair do Lidiflow", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        main.addItem(withTitle: "Lidiflow", action: nil, keyEquivalent: "").submenu = appMenu

        let edit = NSMenu(title: "Editar")
        edit.addItem(NSMenuItem(title: "Desfazer", action: Selector(("undo:")), keyEquivalent: "z"))
        let redo = NSMenuItem(title: "Refazer", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(redo)
        edit.addItem(.separator())
        edit.addItem(NSMenuItem(title: "Recortar", action: #selector(NSText.cut(_:)), keyEquivalent: "x"))
        edit.addItem(NSMenuItem(title: "Copiar", action: #selector(NSText.copy(_:)), keyEquivalent: "c"))
        edit.addItem(NSMenuItem(title: "Colar", action: #selector(NSText.paste(_:)), keyEquivalent: "v"))
        edit.addItem(NSMenuItem(title: "Selecionar Tudo", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a"))
        main.addItem(withTitle: "Editar", action: nil, keyEquivalent: "").submenu = edit

        let window = NSMenu(title: "Janela")
        window.addItem(NSMenuItem(title: "Minimizar", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m"))
        window.addItem(NSMenuItem(title: "Fechar", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w"))
        main.addItem(withTitle: "Janela", action: nil, keyEquivalent: "").submenu = window
        NSApp.windowsMenu = window

        NSApp.mainMenu = main
    }

    @objc private func openMainWindow() {
        mainWindow.show()
    }

    @objc private func copyLast() {
        guard let last = model.history.first else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(last.text, forType: .string)
    }
}
