import SwiftUI

// MARK: - Settings

struct SettingsView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        Page {
            PageHeader(title: "Ajustes", subtitle: "Aparência, idioma, atalhos e permissões.")

            SectionCard(title: "Aparência") {
                SettingRow(title: "Tema", detail: "\u{201C}Sistema\u{201D} acompanha o modo claro/escuro do macOS", divider: false) {
                    Picker("", selection: $model.appearance) {
                        ForEach(Appearance.allCases) { Text($0.title).tag($0) }
                    }
                    .labelsHidden()
                    .pickerStyle(.segmented)
                    .frame(width: 240)
                }
            }

            SectionCard(title: "Ditado") {
                SettingRow(title: "Idioma") {
                    Picker("", selection: $model.language) {
                        Text("Português").tag("pt")
                        Text("English").tag("en")
                        Text("Español").tag("es")
                        Text("Detectar automaticamente").tag("auto")
                    }
                    .labelsHidden()
                    .frame(width: 220)
                }
                SettingRow(title: "Microfone", detail: "Fale algo: as barras mostram se ele está ouvindo. No padrão, com fone Bluetooth o Lidiflow usa o microfone do Mac, e a música não pausa") {
                    MicrophoneTest(selection: model.microphone)
                    MicrophonePicker(selection: $model.microphone, list: model.microphones)
                }
                SettingRow(title: "Sons ao iniciar e parar") {
                    Toggle("", isOn: $model.soundsEnabled).labelsHidden().toggleStyle(.switch)
                }
                SettingRow(title: "Ajustar texto com IA", detail: "Remove vícios de fala e corrige a pontuação", divider: false) {
                    Toggle("", isOn: $model.polishEnabled).labelsHidden().toggleStyle(.switch)
                }
            }

            SectionCard(title: "Instruções para a IA",
                        footer: "Vão junto em todo uso da IA: ajuste de texto, modo comando e transforms.") {
                ZStack(alignment: .topLeading) {
                    TextEditor(text: $model.aiInstructions)
                        .font(.system(size: 14))
                        .scrollContentBackground(.hidden)
                    if model.aiInstructions.isEmpty {
                        Text("Ex.: Escreva em português do Brasil. Use \u{201C}você\u{201D}, nunca \u{201C}tu\u{201D}. Prefira frases curtas.")
                            .font(.system(size: 14))
                            .foregroundStyle(Theme.secondary.opacity(0.7))
                            .padding(.leading, 5)
                            .allowsHitTesting(false)
                    }
                }
                .frame(height: 110)
                .padding(.horizontal, 12)
                .padding(.vertical, 10)
            }

            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    SectionTitle("Atalhos")
                    Spacer()
                    Button("Restaurar padrões") { model.shortcuts = ShortcutAction.defaults }
                        .buttonStyle(.plain)
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(Theme.blue)
                }
                .padding(.horizontal, 4)
                VStack(spacing: 0) {
                    ForEach(ShortcutAction.allCases) { action in
                        ActionShortcutsRow(model: model, action: action, divider: action != ShortcutAction.allCases.last)
                    }
                }
                .card(padding: 0)
                Text(shortcutsFooter)
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.secondary)
                    .padding(.horizontal, 4)
            }

            SectionCard(title: "Permissões") {
                PermissionRow(title: "Acessibilidade", detail: "Para detectar a tecla e colar o texto",
                              granted: model.accessibilityGranted, action: model.openAccessibilitySettings)
                PermissionRow(title: "Microfone", detail: "Para ouvir o que você dita",
                              granted: model.microphoneGranted, divider: false, action: model.requestMicrophone)
            }
        }
    }
}

/// "Padrão do sistema" follows whatever macOS uses, except a Bluetooth headset (see `MicrophoneList.recordingDevice`); a chosen microphone that's unplugged stays listed,
/// marked as disconnected, and the recorder falls back to the default until it's back.
struct MicrophonePicker: View {
    @Binding var selection: String?
    @ObservedObject var list: MicrophoneList
    var width: CGFloat = 220

    var body: some View {
        Picker("", selection: $selection) {
            Text(list.systemDefault.map { "Padrão do sistema (\($0))" } ?? "Padrão do sistema").tag(String?.none)
            Divider()
            ForEach(list.all) { Text($0.name).tag(Optional($0.id)) }
            if let selection, !list.all.contains(where: { $0.id == selection }) {
                Text("Desconectado").tag(Optional(selection))
            }
        }
        .labelsHidden()
        .frame(width: width)
        .onAppear { list.refresh() }
    }
}

/// Live waveform of the chosen microphone while this page is open.
struct MicrophoneTest: View {
    let selection: String?
    @StateObject private var monitor = MicrophoneMonitor()

    var body: some View {
        Group {
            if monitor.failed {
                Image(systemName: "mic.slash").foregroundStyle(Theme.secondary)
                    .help("Não foi possível ouvir este microfone. Confira a permissão em Permissões.")
            } else {
                Waveform(levels: monitor.levels, color: Theme.blue, height: 18)
            }
        }
        // Listens only while you're looking at it: this page open, the window visible and Lidiflow in front.
        // (Closing the window keeps this view alive, so onDisappear alone would leave the mic on.)
        .onAppear(perform: update)
        .onDisappear { monitor.stop() }
        .onChange(of: selection) { if monitor.isRunning { monitor.start() } }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didChangeOcclusionStateNotification)) { _ in update() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in update() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didResignActiveNotification)) { _ in update() }
    }

    private func update() {
        let visible = NSApp.windows.contains { !($0 is NSPanel) && $0.occlusionState.contains(.visible) && $0.isVisible }
        if visible && NSApp.isActive {
            if !monitor.isRunning { monitor.start() }
        } else {
            monitor.stop()
        }
    }
}

private extension SettingsView {
    var shortcutsFooter: String {
        var text = "Até 2 atalhos por ação: uma tecla F, uma combinação (⌃⌥D), um modificador sozinho (⇧ direito, Fn) ou um botão extra do mouse (meio ou laterais). Transforms têm atalhos próprios na aba Transforms."
        let usesFn = model.shortcuts.values.joined().contains { $0.flags.contains(.maskSecondaryFn) }
        if usesFn {
            text += " Com Fn, deixe \u{201C}Ao pressionar 🌐\u{201D} como \u{201C}Não fazer nada\u{201D} em Ajustes do Sistema → Teclado."
        }
        return text
    }
}
