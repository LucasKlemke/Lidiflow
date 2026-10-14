import SwiftUI

struct TransformsView: View {
    @ObservedObject var model: AppModel
    @State private var editing: UUID?
    @State private var confirmReset = false

    private let columns = [GridItem(.adaptive(minimum: 220), spacing: 14)]

    var body: some View {
        Page {
          ScrollViewReader { proxy in
            VStack(alignment: .leading, spacing: 28) {
            HStack(alignment: .center, spacing: 12) {
                Text("Transforms")
                    .font(.system(size: 30, weight: .semibold))
                    .foregroundStyle(Theme.text)
                Text("Beta")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(RoundedRectangle(cornerRadius: 6).fill(Theme.blue))
                Spacer()
                HStack(spacing: 10) {
                    Text("Ativado").foregroundStyle(Theme.text)
                    Image(systemName: "info.circle")
                        .foregroundStyle(Theme.secondary)
                        .help("Desligado, os atalhos dos transforms e a aplicação automática param de funcionar.")
                    Toggle("", isOn: $model.transformsEnabled).labelsHidden().toggleStyle(.switch)
                }
            }

            TransformsHero(
                tryIt: { withAnimation { proxy.scrollTo("playground", anchor: .top) } }
            )

            AutoApplyRow(model: model)
                .disabled(!model.transformsEnabled)
                .opacity(model.transformsEnabled ? 1 : 0.5)

            VStack(alignment: .leading, spacing: 16) {
                HStack(alignment: .firstTextBaseline) {
                    Text("Meus Transforms")
                        .font(Theme.display(26))
                        .foregroundStyle(Theme.text)
                    Spacer()
                    Button { confirmReset = true } label: {
                        Label("Restaurar padrões", systemImage: "arrow.counterclockwise")
                    }
                    .buttonStyle(.plain)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(Theme.text)
                    .padding(.trailing, 8)
                    Button { createNew() } label: {
                        Label("Criar novo", systemImage: "plus")
                    }
                    .buttonStyle(PrimaryButtonStyle())
                }

                LazyVGrid(columns: columns, spacing: 14) {
                    ForEach(Array(model.transforms.enumerated()), id: \.element.id) { index, transform in
                        TransformCard(transform: transform, keys: model.shortcut(for: index)?.keys) {
                            editing = transform.id
                        }
                        .contextMenu {
                            Button("Editar") { editing = transform.id }
                            Button("Mover para o início") {
                                model.transforms.remove(at: index)
                                model.transforms.insert(transform, at: 0)
                            }
                            Button("Apagar", role: .destructive) { model.transforms.remove(at: index) }
                        }
                    }
                    CreateCard(action: createNew)
                }
            }

            TransformPlayground(model: model)
                .id("playground")
            }
          }
        }
        .overlay {
            if let id = editing {
                ZStack {
                    Color.black.opacity(0.4)
                        .contentShape(Rectangle())
                        .onTapGesture { editing = nil }
                    TransformEditor(model: model, id: id) { editing = nil }
                        .clipShape(RoundedRectangle(cornerRadius: 16))
                        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(Theme.border))
                        .shadow(color: .black.opacity(0.3), radius: 30, y: 12)
                        .padding(32)
                }
                .transition(.opacity)
            }
        }
        .animation(.easeOut(duration: 0.15), value: editing)
        .confirmationDialog("Restaurar os transforms padrão?", isPresented: $confirmReset) {
            Button("Restaurar", role: .destructive) { model.transforms = Transform.defaults }
        } message: {
            Text("Seus transforms personalizados serão apagados.")
        }
    }

    private func createNew() {
        let transform = Transform(name: "", prompt: "")
        model.transforms.append(transform)
        editing = transform.id
    }
}

extension UUID: @retroactive Identifiable {
    public var id: UUID { self }
}

// MARK: - Hero

/// "Transforms funcionam onde você escreve", with icons of apps installed on this Mac.
private struct TransformsHero: View {
    let tryIt: () -> Void
    @State private var showingHelp = false

    private static let bundleIDs = [
        "com.tinyspeck.slackmacgap", "com.apple.mail", "com.apple.Notes", "com.apple.MobileSMS",
        "notion.id", "com.google.Chrome", "com.anthropic.claudefordesktop", "com.microsoft.VSCode",
        "com.apple.Safari", "com.hnc.Discord", "net.whatsapp.WhatsApp", "com.linear",
    ]

    private static let icons: [NSImage] = bundleIDs.compactMap { id in
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: id).map { NSWorkspace.shared.icon(forFile: $0.path) }
    }.prefix(7).map { $0 }

    /// Scattered positions for up to seven app bubbles, in points from the hero's top-right.
    private static let spots: [CGPoint] = [
        CGPoint(x: 230, y: 14), CGPoint(x: 120, y: 14), CGPoint(x: 280, y: 92), CGPoint(x: 175, y: 92),
        CGPoint(x: 70, y: 92), CGPoint(x: 230, y: 170), CGPoint(x: 120, y: 170),
    ]

    var body: some View {
        ZStack(alignment: .topTrailing) {
            Theme.heroGradient
            RadialGradient(colors: [Theme.blueGlow.opacity(0.65), .clear], center: .topTrailing, startRadius: 10, endRadius: 360)

            ForEach(Array(Self.icons.enumerated()), id: \.offset) { index, icon in
                Image(nsImage: icon)
                    .resizable()
                    .frame(width: 44, height: 44)
                    .padding(9)
                    .background(Circle().fill(.white.opacity(0.16)))
                    .overlay(Circle().strokeBorder(.white.opacity(0.28)))
                    .shadow(color: .black.opacity(0.18), radius: 8, y: 4)
                    .padding(.top, Self.spots[index].y)
                    .padding(.trailing, Self.spots[index].x - 40)
            }

            VStack(alignment: .leading, spacing: 12) {
                Text("Transforms funcionam onde você escreve")
                    .font(Theme.display(30))
                    .foregroundStyle(.white)
                    .fixedSize(horizontal: false, vertical: true)
                Text("Reescreva, limpe ou reorganize um texto depois de ditar, em qualquer app.")
                    .font(.system(size: 14))
                    .foregroundStyle(.white.opacity(0.88))
                    .frame(maxWidth: 380, alignment: .leading)
                HStack(spacing: 18) {
                    Button("Experimentar", action: tryIt)
                        .buttonStyle(.plain)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Theme.blueDeep)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 8)
                        .background(RoundedRectangle(cornerRadius: 8).fill(.white))
                    Button("Como funciona") { showingHelp = true }
                        .buttonStyle(.plain)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(.white)
                        .popover(isPresented: $showingHelp, arrowEdge: .bottom) { HowItWorks() }
                }
                .padding(.top, 6)
            }
            .frame(maxWidth: 440, alignment: .leading)
            .padding(30)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        }
        .frame(height: 240)
        .clipShape(RoundedRectangle(cornerRadius: 16))
        .shadow(color: Theme.blue.opacity(0.22), radius: 18, y: 8)
    }
}

private struct HowItWorks: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Como funciona").font(.system(size: 14, weight: .semibold))
            step("1", "Selecione um texto em qualquer app (email, Slack, Notion…).")
            step("2", "Aperte o atalho do transform, como Fn + 1.")
            step("3", "A IA reescreve e o texto selecionado é substituído.")
            Text("Ou ligue \u{201C}Aplicar automaticamente\u{201D} para transformar tudo o que você dita.")
                .font(.system(size: 12))
                .foregroundStyle(Theme.secondary)
        }
        .padding(18)
        .frame(width: 320)
    }

    private func step(_ number: String, _ text: String) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Text(number)
                .font(.system(size: 11, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 20, height: 20)
                .background(Circle().fill(Theme.blue))
            Text(text).font(.system(size: 13)).fixedSize(horizontal: false, vertical: true)
        }
    }
}

// MARK: - Auto apply

private struct AutoApplyRow: View {
    @ObservedObject var model: AppModel

    private var selection: Binding<UUID> {
        Binding(
            get: { model.autoTransform?.id ?? model.autoTransformID ?? model.transforms.first?.id ?? UUID() },
            set: { model.autoTransformID = $0 }
        )
    }

    var body: some View {
        HStack(spacing: 16) {
            VStack(alignment: .leading, spacing: 3) {
                Text("Aplicar automaticamente após ditar")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(Theme.text)
                Text("Roda um transform em tudo o que você dita, antes de colar.")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.secondary)
            }
            Spacer()
            Image(systemName: "info.circle")
                .foregroundStyle(Theme.secondary)
                .help("Usa o modelo de IA escolhido em Modelos. Deixa o ditado um pouco mais lento.")
            PopUpPicker(items: model.transforms.map { .init(value: $0.id, title: $0.name.isEmpty ? "Sem nome" : $0.name) },
                        selection: selection)
                .frame(width: 200)
                .disabled(!model.autoTransformEnabled)
            Toggle("", isOn: $model.autoTransformEnabled)
                .labelsHidden()
                .toggleStyle(.switch)
        }
        .card()
    }
}

// MARK: - Cards

private struct TransformCard: View {
    let transform: Transform
    let keys: [String]?
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 10) {
                if let keys {
                    KeyCaps(keys: keys)
                } else {
                    Text("Sem atalho")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(Theme.secondary)
                        .padding(.vertical, 3)
                }
                Text(transform.name.isEmpty ? "Sem nome" : transform.name)
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(Theme.text)
                    .lineLimit(1)
                Text(transform.prompt.isEmpty ? "Sem instrução" : transform.prompt)
                    .font(.system(size: 13))
                    .foregroundStyle(Theme.secondary)
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                Spacer(minLength: 0)
            }
            .padding(18)
            .frame(maxWidth: .infinity, minHeight: 140, alignment: .topLeading)
            .background(RoundedRectangle(cornerRadius: 14).fill(Theme.card))
            .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(hovering ? Theme.blue.opacity(0.5) : Theme.border))
            .shadow(color: .black.opacity(hovering ? 0.08 : 0), radius: 10, y: 4)
            .offset(y: hovering ? -2 : 0)
            .contentShape(RoundedRectangle(cornerRadius: 14))
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.15), value: hovering)
    }
}

private struct CreateCard: View {
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 10) {
                Image(systemName: "plus")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Theme.blue)
                    .frame(width: 26, height: 22)
                    .background(RoundedRectangle(cornerRadius: 6).fill(Theme.blueTint))
                Text("Criar o seu")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(Theme.text)
                Text("Escreva sua própria instrução")
                    .font(.system(size: 13))
                    .foregroundStyle(Theme.secondary)
                Spacer(minLength: 0)
            }
            .padding(18)
            .frame(maxWidth: .infinity, minHeight: 140, alignment: .topLeading)
            .background(RoundedRectangle(cornerRadius: 14).fill(hovering ? Theme.blueTint : Color.clear))
            .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(hovering ? Theme.blue.opacity(0.5) : Theme.border,
                                                                      style: StrokeStyle(lineWidth: 1, dash: [5, 4])))
            .contentShape(RoundedRectangle(cornerRadius: 14))
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

// MARK: - Editor

/// Two-pane editor that saves as you type, like Wispr Flow's "Create your own".
private struct TransformEditor: View {
    @ObservedObject var model: AppModel
    let id: UUID
    let close: () -> Void
    @State private var original: Transform?
    @State private var escMonitor: Any?

    private var index: Int? { model.transforms.firstIndex { $0.id == id } }

    var body: some View {
        Group {
            if let index {
                content(index)
            } else {
                Color.clear.onAppear(perform: close)
            }
        }
        .frame(maxWidth: 820, maxHeight: 560)
        .onAppear {
            original = index.map { model.transforms[$0] }
            // Esc closes the dialog, unless it's being used to cancel a shortcut recording.
            escMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
                guard event.keyCode == 53, !model.isRecordingShortcut else { return event }
                close()
                return nil
            }
        }
        .onDisappear {
            if let escMonitor { NSEvent.removeMonitor(escMonitor) }
            discardIfEmpty()
        }
    }

    private func content(_ index: Int) -> some View {
        let transform = $model.transforms[index]
        let isNew = original?.name.isEmpty == true && original?.prompt.isEmpty == true
        return HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 16) {
                Text(isNew ? "Crie o seu" : (transform.wrappedValue.name.isEmpty ? "Sem nome" : transform.wrappedValue.name))
                    .font(Theme.display(32))
                    .foregroundStyle(Theme.text)
                    .lineLimit(2)
                Text("Dê um nome, escolha um atalho e diga como este transform deve mudar o seu texto.")
                    .font(.system(size: 14))
                    .foregroundStyle(Theme.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer()
                if !isNew {
                    Button(role: .destructive) {
                        model.transforms.remove(at: index)
                        close()
                    } label: {
                        Label("Apagar transform", systemImage: "trash")
                    }
                    .buttonStyle(.plain)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(.red)
                }
            }
            .padding(32)
            .frame(width: 280, alignment: .topLeading)
            .frame(maxHeight: .infinity, alignment: .topLeading)
            .background(Theme.card)

            VStack(alignment: .leading, spacing: 16) {
                HStack(spacing: 14) {
                    Spacer()
                    Label("Salvamento automático", systemImage: "checkmark.circle")
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.secondary)
                    Button {
                        if let original { transform.wrappedValue = original }
                    } label: {
                        Label("Desfazer alterações", systemImage: "arrow.counterclockwise")
                    }
                    .buttonStyle(.plain)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(Theme.text)
                    .disabled(original == transform.wrappedValue)
                    Button("Concluir", action: close)
                        .buttonStyle(PrimaryButtonStyle())
                        .keyboardShortcut(.defaultAction)
                }

                EditorSection(title: "Nome do transform") {
                    TextField("Ex.: Modo chefe", text: transform.name)
                        .textFieldStyle(.plain)
                        .font(.system(size: 14))
                        .editorField()
                }

                EditorSection(title: "Atalho") {
                    TransformShortcutField(model: model, index: index, shortcut: transform.shortcut)
                }

                EditorSection(title: "Instrução") {
                    ZStack(alignment: .topLeading) {
                        TextEditor(text: transform.prompt)
                            .font(.system(size: 14))
                            .scrollContentBackground(.hidden)
                            .scrollIndicators(.never)
                        if transform.wrappedValue.prompt.isEmpty {
                            Text("Como este transform deve mudar o seu texto?")
                                .font(.system(size: 14))
                                .foregroundStyle(Theme.secondary.opacity(0.7))
                                .padding(.leading, 5)
                                .allowsHitTesting(false)
                        }
                    }
                    .frame(maxHeight: .infinity)
                    .editorField()
                }
                .frame(maxHeight: .infinity)
            }
            .padding(24)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Theme.background)
        }
    }

    /// A new transform closed without a name or instruction isn't worth keeping.
    private func discardIfEmpty() {
        guard let index else { return }
        let transform = model.transforms[index]
        if transform.name.trimmingCharacters(in: .whitespaces).isEmpty,
           transform.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            model.transforms.remove(at: index)
        } else if transform.name.isEmpty {
            model.transforms[index].name = "Sem nome"
        }
    }
}

private struct EditorSection<Content: View>: View {
    let title: String
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Theme.text)
            content
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 12).fill(Theme.hover))
    }
}

private extension View {
    func editorField() -> some View {
        self
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 8).fill(Theme.card))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Theme.border))
    }
}

/// "Clique para adicionar um atalho" field: records a key combo or mouse button.
private struct TransformShortcutField: View {
    @ObservedObject var model: AppModel
    let index: Int
    @Binding var shortcut: KeyCombo?
    @StateObject private var capture = ShortcutCapture()

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 10) {
                Button(action: toggle) {
                    HStack(spacing: 8) {
                        if capture.isRecording {
                            Image(systemName: "record.circle").foregroundStyle(Theme.blue)
                            Text("Pressione a tecla, combinação ou botão do mouse…").foregroundStyle(Theme.blue)
                        } else if let combo = shortcut {
                            KeyCaps(keys: combo.keys)
                        } else if let fallback = model.defaultShortcut(for: index) {
                            KeyCaps(keys: fallback.keys)
                            Text("padrão").font(.system(size: 12)).foregroundStyle(Theme.secondary)
                        } else {
                            Text("Clique para adicionar um atalho").foregroundStyle(Theme.secondary)
                        }
                        Spacer(minLength: 0)
                        Image(systemName: capture.isRecording ? "xmark" : "pencil")
                            .foregroundStyle(Theme.secondary)
                    }
                    .font(.system(size: 13))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                if shortcut != nil, !capture.isRecording {
                    Button("Usar padrão") { shortcut = nil }
                        .buttonStyle(.plain)
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(Theme.blue)
                }
            }
            .editorField()
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(capture.isRecording ? Theme.blue : .clear))

            if let message = capture.message {
                Label(message, systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 12))
                    .foregroundStyle(.orange)
            }
        }
        .onDisappear { capture.stop() }
    }

    private func toggle() {
        if capture.isRecording {
            capture.stop()
            return
        }
        capture.start(
            model: model,
            options: .init(allowModifierOnly: false),
            validate: { model.conflict(for: $0, in: .transform(index)) },
            onCapture: { shortcut = $0 }
        )
    }
}

// MARK: - Playground

/// Try transforms on any text, with AI-suggested ones for that text.
private struct TransformPlayground: View {
    @ObservedObject var model: AppModel
    @State private var text = ""
    @State private var versions: [String] = []
    @State private var suggestions: [Transform] = []
    @State private var customPrompt = ""
    @State private var working: String?
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Testar").font(Theme.display(26)).foregroundStyle(Theme.text)
                Text("Cole um texto e aplique um transform, uma sugestão da IA ou uma instrução sua.")
                    .font(.system(size: 13))
                    .foregroundStyle(Theme.secondary)
            }

            VStack(alignment: .leading, spacing: 0) {
                ZStack(alignment: .topLeading) {
                    TextEditor(text: $text)
                        .font(.system(size: 14))
                        .scrollContentBackground(.hidden)
                        .scrollIndicators(.never)
                    if text.isEmpty {
                        Text("Cole ou dite um texto aqui…")
                            .font(.system(size: 14))
                            .foregroundStyle(Theme.secondary.opacity(0.7))
                            .padding(.leading, 5)
                            .allowsHitTesting(false)
                    }
                }
                .padding(14)
                .frame(minHeight: 150)
                Divider().overlay(Theme.border)
                HStack(spacing: 8) {
                    Button { text = versions.removeLast() } label: { Label("Desfazer", systemImage: "arrow.uturn.backward") }
                        .buttonStyle(SecondaryButtonStyle())
                        .disabled(versions.isEmpty || working != nil)
                    Button { copyToClipboard(text) } label: { Label("Copiar", systemImage: "doc.on.doc") }
                        .buttonStyle(SecondaryButtonStyle())
                        .disabled(text.isEmpty)
                    Button { suggest() } label: { Label("Sugerir", systemImage: "sparkles") }
                        .buttonStyle(SecondaryButtonStyle())
                        .disabled(text.isEmpty || working != nil)
                    if let working {
                        ProgressView().controlSize(.small).padding(.leading, 4)
                        Text(working).font(.system(size: 12)).foregroundStyle(Theme.secondary)
                    } else if let error {
                        Label(error, systemImage: "exclamationmark.triangle.fill")
                            .font(.system(size: 12))
                            .foregroundStyle(.orange)
                            .lineLimit(1)
                    }
                    Spacer()
                }
                .padding(12)
            }
            .card(padding: 0)

            FlowLayout(spacing: 8) {
                ForEach(model.transforms) { transform in
                    Chip(title: transform.name) { apply(transform.prompt, label: transform.name) }
                        .help(transform.prompt)
                }
                ForEach(suggestions) { suggestion in
                    Chip(title: suggestion.name, systemImage: "sparkles") { apply(suggestion.prompt, label: suggestion.name) }
                        .help(suggestion.prompt)
                        .contextMenu {
                            Button("Salvar nos meus transforms") { model.transforms.append(suggestion) }
                        }
                }
            }

            HStack(spacing: 8) {
                InlineField(prompt: "Ou descreva a transformação (ex.: \u{201C}deixa mais empolgante\u{201D})", text: $customPrompt, onSubmit: applyCustom)
                Button("Aplicar", action: applyCustom)
                    .buttonStyle(PrimaryButtonStyle())
                    .disabled(customPrompt.isEmpty || text.isEmpty || working != nil)
            }
        }
    }

    private func applyCustom() {
        apply(customPrompt, label: "Aplicando")
        customPrompt = ""
    }

    private func apply(_ prompt: String, label: String) {
        guard !text.isEmpty, working == nil else { return }
        let original = text
        working = "\(label)…"
        error = nil
        Task { @MainActor in
            defer { working = nil }
            do {
                let result = try await LLM.transform(original, with: prompt)
                versions.append(original)
                text = result
            } catch {
                self.error = (error as? LocalizedError)?.errorDescription ?? "Falha ao transformar"
            }
        }
    }

    private func suggest() {
        working = "Gerando sugestões…"
        error = nil
        let content = text
        Task { @MainActor in
            defer { working = nil }
            do {
                suggestions = try await LLM.suggestTransforms(for: content)
            } catch {
                self.error = (error as? LocalizedError)?.errorDescription ?? "Falha ao sugerir"
            }
        }
    }
}
