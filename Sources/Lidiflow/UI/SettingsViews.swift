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
                SettingRow(title: "Sons ao iniciar e parar") {
                    Toggle("", isOn: $model.soundsEnabled).labelsHidden().toggleStyle(.switch)
                }
                SettingRow(title: "Ajustar texto com IA", detail: "Remove vícios de fala e corrige a pontuação", divider: false) {
                    Toggle("", isOn: $model.polishEnabled).labelsHidden().toggleStyle(.switch)
                }
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
