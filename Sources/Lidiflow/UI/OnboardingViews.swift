import AppKit
import AVFoundation
import Combine
import SwiftUI

/// First-launch setup: permissions, microphone and a transcription model that works without the Terminal.
final class OnboardingWindowController: NSObject, NSWindowDelegate {
    private let model: AppModel
    private let onFinish: () -> Void
    private var window: NSWindow?

    init(model: AppModel, onFinish: @escaping () -> Void) {
        self.model = model
        self.onFinish = onFinish
    }

    /// Needed until it's finished once. Someone who already set Lidiflow up before it existed skips it.
    /// `open Lidiflow.app --args --onboarding` shows it again on an already set-up Mac, and
    /// `--onboarding-demo` shows it as a new Mac sees it (nothing granted, the model downloading).
    static var isNeeded: Bool {
        if CommandLine.arguments.contains("--onboarding") || OnboardingView.demo { return true }
        guard !Settings.onboardingDone else { return false }
        if AXIsProcessTrusted() && AVCaptureDevice.authorizationStatus(for: .audio) == .authorized {
            Settings.onboardingDone = true
            return false
        }
        return true
    }

    func show() {
        if window == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 720, height: 620),
                styleMask: [.titled, .closable, .fullSizeContentView],
                backing: .buffered,
                defer: false
            )
            window.title = "Bem-vindo ao Lidiflow"
            window.titleVisibility = .hidden
            window.titlebarAppearsTransparent = true
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: OnboardingView(model: model, manager: model.models) { [weak self] in
                self?.finish()
            })
            window.center()
            window.delegate = self
            self.window = window
        }
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func finish() {
        Settings.onboardingDone = true
        window?.delegate = nil
        window?.close()
        window = nil
        onFinish()
    }

    /// Closing it early still opens the app; it comes back on the next launch until it's finished.
    func windowWillClose(_ notification: Notification) {
        window = nil
        onFinish()
    }
}

/// One screen, one checklist: each item shows what's left to do and turns green when it's done.
private struct OnboardingView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var manager: ModelManager
    let finish: () -> Void

    static let demo = CommandLine.arguments.contains("--onboarding-demo")
    @State private var demoProgress = 0.0
    @State private var choosing = false

    private let permissionTimer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()
    private var item: LocalModel { ModelManager.recommended }
    private var microphoneGranted: Bool { !Self.demo && model.microphoneGranted }
    private var accessibilityGranted: Bool { !Self.demo && model.accessibilityGranted }
    /// The chosen model is downloaded, or the chosen provider has a key.
    private var modelReady: Bool {
        if Self.demo { return demoProgress >= 1 }
        return model.transcriptionEngine == .local ? manager.installed.contains(model.localModel)
                                                   : model.savedKeys.contains(model.transcriptionProvider)
    }
    /// The model being downloaded: the chosen one, or the recommended one fetched on its own.
    private var downloading: LocalModel? {
        guard !Self.demo else { return modelReady ? nil : item }
        guard model.transcriptionEngine == .local, !modelReady else { return nil }
        return [model.localModel, item.id].lazy.filter { manager.progress[$0] != nil }.compactMap(ModelManager.model(id:)).first
    }
    private var progress: Double? { Self.demo ? (modelReady ? nil : demoProgress) : downloading.flatMap { manager.progress[$0.id] } }
    private var allDone: Bool { microphoneGranted && accessibilityGranted && modelReady }

    var body: some View {
        VStack(spacing: 0) {
            VStack(spacing: 24) {
                VStack(spacing: 10) {
                    Image(nsImage: NSApp.applicationIconImage)
                        .resizable()
                        .frame(width: 72, height: 72)
                    Text("Bem-vindo ao Lidiflow")
                        .font(Theme.display(28))
                        .foregroundStyle(Theme.text)
                    Text("Segure \(model.dictateLabel), fale e solte: o texto aparece onde o cursor estiver. Falta só isto:")
                        .font(.system(size: 14))
                        .foregroundStyle(Theme.secondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }

                VStack(spacing: 0) {
                    microphone
                    accessibility
                    transcription
                }
                .card(padding: 0)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .padding(.horizontal, 40)
            .padding(.top, 36)

            HStack {
                Text(allDone ? "Tudo pronto." : "Dá para terminar depois em Ajustes e Modelos.")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.secondary)
                Spacer()
                Button(allDone ? "Começar a usar" : "Fazer depois", action: finish)
                    .buttonStyle(allDone ? AnyButtonStyle(PrimaryButtonStyle()) : AnyButtonStyle(SecondaryButtonStyle()))
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 16)
            .overlay(alignment: .top) { Divider().overlay(Theme.border) }
        }
        .frame(width: 720, height: 620)
        .background(Theme.background)
        .tint(Theme.blue)
        .onReceive(permissionTimer) { _ in
            model.refreshPermissions()
            if Self.demo { demoProgress = min(demoProgress + 0.04, 1) }
        }
        .onAppear(perform: prepareModel)
        // Apple's files are per language: changing it means fetching the new one.
        .onChange(of: model.language) { manager.refresh() }
        .onChange(of: manager.installed) { prepareModel() }
    }

    // MARK: Items

    private var microphone: some View {
        ChecklistItem(done: microphoneGranted, title: "Liberar o microfone",
                      detail: microphoneGranted ? "Fale para testar" : "Só ouve enquanto você segura o atalho") {
            if microphoneGranted {
                MicrophoneTest(selection: model.microphone)
                MicrophonePicker(selection: $model.microphone, list: model.microphones, width: 170)
            } else if !Self.demo && AVCaptureDevice.authorizationStatus(for: .audio) == .denied {
                Button("Abrir Ajustes", action: model.openMicrophoneSettings).buttonStyle(PrimaryButtonStyle())
            } else {
                Button("Permitir", action: model.requestMicrophone).buttonStyle(PrimaryButtonStyle())
            }
        }
    }

    private var accessibility: some View {
        ChecklistItem(done: accessibilityGranted, title: "Liberar a Acessibilidade",
                      detail: accessibilityGranted ? "Para perceber o atalho e colar o texto"
                                                   : "Clique em Abrir Ajustes e ative o Lidiflow na lista") {
            if !accessibilityGranted {
                Button("Abrir Ajustes", action: model.requestAccessibility).buttonStyle(PrimaryButtonStyle())
            }
        }
    }

    private var transcription: some View {
        ChecklistItem(done: modelReady, title: "Preparar a transcrição", detail: transcriptionDetail, divider: false) {
            if let progress {
                ProgressView(value: progress).frame(width: 110)
            }
            Button(modelReady ? "Trocar" : "Escolher") { choosing = true }
                .buttonStyle(SecondaryButtonStyle())
        }
        .sheet(isPresented: $choosing) {
            ModelChooser(model: model, manager: manager) { choosing = false }
        }
    }

    private var transcriptionDetail: String {
        if let progress, let downloading {
            let percent = progress.formatted(.percent.precision(.fractionLength(0)))
            // Apple's size isn't known up front: macOS manages those files.
            return downloading.engine == .apple ? "Baixando o modelo da Apple… \(percent)"
                                                : "Baixando o \(downloading.name)… \(percent) de \(downloading.size)"
        }
        if model.transcriptionEngine == .cloud {
            let provider = model.transcriptionProvider
            return modelReady ? "\(provider.title) · \(model.transcriptionModel), com a sua chave"
                              : "Falta a chave da \(provider.title): clique em Escolher"
        }
        if modelReady, let selected = ModelManager.selected {
            return "\(selected.name) · no seu Mac, grátis e offline"
        }
        return manager.lastError ?? "Preparando…"
    }

    /// Without a usable model, downloads the recommended one by itself (Apple's when the Mac has it,
    /// otherwise one that runs inside the app) and uses it when it's ready. Someone who picked another
    /// model, or an API key, keeps their choice.
    private func prepareModel() {
        guard !Self.demo, model.transcriptionEngine == .local, !manager.installed.contains(model.localModel) else { return }
        if manager.installed.contains(item.id) {
            model.localModel = item.id
        } else if manager.progress[item.id] == nil {
            manager.download(item)
        }
    }
}

/// The same transcription choices as Modelos, in a sheet: a model already on the Mac, one to download,
/// or an API key.
private struct ModelChooser: View {
    @ObservedObject var model: AppModel
    @ObservedObject var manager: ModelManager
    let done: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Modelo de transcrição")
                            .font(Theme.display(24))
                            .foregroundStyle(Theme.text)
                        Text("Use um que você já tem, baixe outro ou conecte sua chave de API. Dá para trocar depois em Modelos.")
                            .font(.system(size: 13))
                            .foregroundStyle(Theme.secondary)
                    }
                    SectionCard {
                        SettingRow(title: "Idioma em que você vai falar", divider: false) {
                            Picker("", selection: $model.language) {
                                Text("Português").tag("pt")
                                Text("English").tag("en")
                                Text("Español").tag("es")
                            }
                            .labelsHidden()
                            .frame(width: 160)
                        }
                    }
                    TranscriptionCard(model: model, manager: manager)
                }
                .padding(28)
            }
            HStack {
                Text("Baixar um modelo não troca o que está em uso: clique em Usar quando terminar.")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.secondary)
                Spacer()
                Button("Pronto", action: done)
                    .buttonStyle(PrimaryButtonStyle())
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 16)
            .overlay(alignment: .top) { Divider().overlay(Theme.border) }
        }
        .frame(width: 720, height: 580)
        .background(Theme.background)
        .onAppear { manager.refresh() }
    }
}

/// A row with a check that turns green when done, and the action (if any) on the right.
private struct ChecklistItem<Trailing: View>: View {
    let done: Bool
    let title: String
    let detail: String
    var divider = true
    @ViewBuilder let trailing: Trailing

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                Image(systemName: done ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 20))
                    .foregroundStyle(done ? Color.green : Theme.secondary.opacity(0.5))
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.system(size: 14, weight: .semibold)).foregroundStyle(Theme.text)
                    Text(detail).font(.system(size: 12)).foregroundStyle(Theme.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 12)
                trailing
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 14)
            .animation(.easeOut(duration: 0.2), value: done)
            if divider { Divider().overlay(Theme.border).padding(.leading, 48) }
        }
    }
}

private struct AnyButtonStyle: ButtonStyle {
    private let make: (Configuration) -> AnyView

    init<S: ButtonStyle>(_ style: S) {
        make = { AnyView(style.makeBody(configuration: $0)) }
    }

    func makeBody(configuration: Configuration) -> some View { make(configuration) }
}
