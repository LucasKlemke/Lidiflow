import SwiftUI

// MARK: - Models

struct ModelsView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var manager: ModelManager

    var body: some View {
        Page {
            PageHeader(title: "Modelos", accent: "do seu jeito",
                       subtitle: "Rode tudo no seu Mac, de graça, ou use uma conta que você já tem.")

            ModelCard(
                title: "Transcrição",
                subtitle: "Transforma sua voz em texto",
                local: Binding(get: { model.transcriptionEngine == .local },
                               set: { model.transcriptionEngine = $0 ? .local : .cloud }),
                localDetail: "No seu Mac · privado e grátis",
                cloudDetail: "OpenAI ou Groq"
            ) {
                if model.transcriptionEngine == .local {
                    ForEach(ModelManager.catalog) { item in
                        LocalModelRow(item: item, model: model, manager: manager,
                                      divider: item.id != ModelManager.catalog.last?.id)
                    }
                    if let error = manager.lastError { ErrorRow(message: error) }
                } else {
                    ProviderRow(selection: $model.transcriptionProvider, providers: CloudTranscriber.providers)
                    APIKeyRow(model: model, provider: model.transcriptionProvider)
                    ControlRow(divider: false) {
                        RowTitle(title: "Modelo")
                    } control: {
                        ModelMenu(selection: $model.transcriptionModel,
                                  options: CloudTranscriber.models(for: model.transcriptionProvider))
                    }
                }
            }

            AIModelCard(model: model, ollama: model.ollama)
        }
        .onAppear { manager.refresh() }
    }
}

/// A model card that starts with the one question that matters: on this Mac, or on my account?
private struct ModelCard<Content: View>: View {
    let title: String
    let subtitle: String
    @Binding var local: Bool
    let localDetail: String
    let cloudDetail: String
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle(title).padding(.leading, 4)
            VStack(alignment: .leading, spacing: 0) {
                VStack(alignment: .leading, spacing: 12) {
                    Text(subtitle).font(.system(size: 13)).foregroundStyle(Theme.secondary)
                    HStack(alignment: .top, spacing: 10) {
                        RunModeOption(selected: local, icon: "laptopcomputer", title: "No meu Mac", detail: localDetail) {
                            local = true
                        }
                        RunModeOption(selected: !local, icon: "key.fill", title: "Minha conta (chave de API)", detail: cloudDetail) {
                            local = false
                        }
                    }
                    .fixedSize(horizontal: false, vertical: true)
                }
                .padding(16)
                Divider().overlay(Theme.border)
                content
            }
            .card(padding: 0)
        }
    }
}

private struct RunModeOption: View {
    let selected: Bool
    let icon: String
    let title: String
    let detail: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                    .font(.system(size: 16))
                    .foregroundStyle(selected ? Theme.blue : Theme.secondary.opacity(0.6))
                    .frame(width: 18, height: 18)
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 8) {
                        Image(systemName: icon)
                            .font(.system(size: 13))
                            .foregroundStyle(selected ? Theme.blue : Theme.secondary)
                            .frame(width: 18)
                        Text(title)
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(Theme.text)
                    }
                    .frame(height: 18)
                    Text(detail)
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.secondary)
                        .padding(.leading, 26)
                }
                Spacer(minLength: 0)
            }
            .padding(14)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(RoundedRectangle(cornerRadius: 10).fill(selected ? Theme.blueTint : Theme.background))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(selected ? Theme.blue.opacity(0.6) : Theme.border))
            .contentShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
    }
}

/// Every control on the right of a model card shares this width so the column lines up.
private let controlWidth: CGFloat = 340

/// A row with a label on the left and a fixed-width control column on the right.
private struct ControlRow<Leading: View, Control: View>: View {
    var divider = true
    @ViewBuilder let leading: Leading
    @ViewBuilder let control: Control

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 16) {
                leading
                    .frame(maxWidth: .infinity, alignment: .leading)
                control
                    .frame(width: controlWidth, alignment: .leading)
            }
            .padding(.horizontal, 16)
            .frame(minHeight: 56)
            if divider { Divider().overlay(Theme.border).padding(.leading, 16) }
        }
    }
}

private struct RowTitle: View {
    let title: String
    var detail: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).foregroundStyle(Theme.text)
            if let detail {
                Text(detail).font(.system(size: 12)).foregroundStyle(Theme.secondary)
            }
        }
    }
}

/// The provider's logo, bundled in Resources/ProviderIcons.
struct ProviderIcon: View {
    let provider: LLMProvider
    var size: CGFloat = 18

    var body: some View {
        Group {
            if let image = Self.image(for: provider) {
                Image(nsImage: image).resizable().interpolation(.high)
            } else {
                Image(systemName: "sparkles").foregroundStyle(Theme.blue)
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: size * 0.22))
    }

    private static var cache: [LLMProvider: NSImage] = [:]

    static func image(for provider: LLMProvider) -> NSImage? {
        if let cached = cache[provider] { return cached }
        guard let url = Bundle.main.url(forResource: provider.rawValue, withExtension: "png", subdirectory: "ProviderIcons"),
              let image = NSImage(contentsOf: url) else { return nil }
        cache[provider] = image
        return image
    }

    /// A small copy for NSMenu items, which draw images at their own size.
    static func menuImage(for provider: LLMProvider) -> NSImage? {
        guard let image = image(for: provider) else { return nil }
        let size = NSSize(width: 16, height: 16)
        return NSImage(size: size, flipped: false) { rect in
            NSBezierPath(roundedRect: rect, xRadius: 3.5, yRadius: 3.5).addClip()
            image.draw(in: rect)
            return true
        }
    }
}

/// Provider pop-up with each provider's logo.
private struct ProviderMenu: View {
    @Binding var selection: LLMProvider
    let providers: [LLMProvider]

    var body: some View {
        PopUpPicker(
            items: providers.map { .init(value: $0, title: $0.title, image: ProviderIcon.menuImage(for: $0)) },
            selection: $selection
        )
        .frame(width: controlWidth)
    }
}

/// Model ID pop-up.
private struct ModelMenu: View {
    @Binding var selection: String
    let options: [String]
    var loading = false

    var body: some View {
        HStack(spacing: 8) {
            PopUpPicker(items: options.map { .init(value: $0, title: $0) }, selection: $selection)
            if loading { ProgressView().controlSize(.small) }
        }
        .frame(width: controlWidth)
    }
}

private struct ProviderRow: View {
    @Binding var selection: LLMProvider
    let providers: [LLMProvider]

    var body: some View {
        ControlRow {
            RowTitle(title: "Provedor")
        } control: {
            ProviderMenu(selection: $selection, providers: providers)
        }
    }
}

/// The provider's API key, right where it's needed.
private struct APIKeyRow: View {
    @ObservedObject var model: AppModel
    let provider: LLMProvider
    @State private var key = ""
    @State private var editing = false
    @State private var failed = false

    private var saved: Bool { model.savedKeys.contains(provider) }

    var body: some View {
        ControlRow {
            VStack(alignment: .leading, spacing: 2) {
                Text("Chave de API").foregroundStyle(Theme.text)
                if failed {
                    Text("O Keychain não permitiu salvar. Tente de novo e clique em Permitir.")
                        .font(.system(size: 12))
                        .foregroundStyle(.orange)
                } else if saved && !editing {
                    Text("Guardada no Keychain do macOS").font(.system(size: 12)).foregroundStyle(Theme.secondary)
                } else if let url = provider.keyURL {
                    Link("Onde pego minha chave?", destination: url)
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.blue)
                }
            }
        } control: {
            if saved && !editing {
                HStack(spacing: 8) {
                    HStack(spacing: 6) {
                        Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                        Text("Conectado").foregroundStyle(Theme.text)
                    }
                    .font(.system(size: 13, weight: .medium))
                    Spacer()
                    Button("Trocar") { editing = true }.buttonStyle(SecondaryButtonStyle())
                    Button { failed = !model.saveKey("", for: provider) } label: { Image(systemName: "trash") }
                        .buttonStyle(.plain)
                        .foregroundStyle(Theme.secondary)
                        .help("Remover chave")
                }
            } else {
                HStack(spacing: 8) {
                    SecureField(provider.keyPlaceholder, text: $key)
                        .textFieldStyle(.plain)
                        .font(.system(size: 13))
                        .padding(.horizontal, 10)
                        .frame(height: 32)
                        .background(RoundedRectangle(cornerRadius: 8).fill(Theme.background))
                        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Theme.border))
                        .onSubmit(save)
                    Button("Salvar", action: save)
                        .buttonStyle(PrimaryButtonStyle())
                        .disabled(key.isEmpty)
                }
            }
        }
        .onChange(of: provider) { _, _ in
            key = ""
            editing = false
            failed = false
        }
    }

    private func save() {
        guard !key.isEmpty else { return }
        failed = !model.saveKey(key, for: provider)
        guard !failed else { return }
        key = ""
        editing = false
    }
}

private struct ErrorRow: View {
    let message: String

    var body: some View {
        Label(message, systemImage: "exclamationmark.triangle.fill")
            .font(.system(size: 12))
            .foregroundStyle(.orange)
            .padding(16)
    }
}

// MARK: - AI model

private struct AIModelCard: View {
    @ObservedObject var model: AppModel
    @ObservedObject var ollama: OllamaManager
    @State private var fetched: [String] = []
    @State private var loading = false
    @State private var error: String?

    private var provider: LLMProvider { model.llmProvider }
    private static let cloudProviders = LLMProvider.allCases.filter(\.needsKey)

    private var modelOptions: [String] {
        var options = provider.suggestedModels
        for id in fetched where !options.contains(id) { options.append(id) }
        if !model.llmModel.isEmpty, !options.contains(model.llmModel) { options.insert(model.llmModel, at: 0) }
        return options
    }

    var body: some View {
        ModelCard(
            title: "IA",
            subtitle: "Limpa o texto, responde ao modo comando e faz os transforms",
            local: Binding(get: { model.aiRunsLocally }, set: { model.aiRunsLocally = $0 }),
            localDetail: "Ollama · privado e grátis",
            cloudDetail: "OpenAI, Claude, Groq, Gemini ou Grok"
        ) {
            if provider == .ollama {
                ollamaRows
            } else {
                cloudRows
            }
        }
        .task(id: provider) {
            fetched = []
            error = nil
            if provider == .ollama {
                await ollama.refresh()
            } else if model.savedKeys.contains(provider) {
                await loadModels()
            }
        }
        .onChange(of: model.savedKeys) { _, keys in
            if keys.contains(provider) { Task { await loadModels() } }
        }
    }

    @ViewBuilder private var ollamaRows: some View {
        SettingRow(title: ollama.isRunning ? "Ollama rodando" : "Ollama parado",
                   detail: ollama.isRunning ? "Baixe um modelo abaixo e clique em Usar" : "Inicie para baixar e usar modelos") {
            if ollama.isRunning {
                Circle().fill(Color.green).frame(width: 8, height: 8)
            } else {
                Button("Iniciar") { Task { await ollama.start() } }
                    .buttonStyle(PrimaryButtonStyle())
            }
        }
        ForEach(OllamaManager.catalog) { item in
            OllamaModelRow(item: item, model: model, ollama: ollama)
        }
        ForEach(ollama.installed.filter { name in !OllamaManager.catalog.contains { name == $0.id || name == $0.id + ":latest" } }, id: \.self) { name in
            InstalledOllamaRow(name: name, model: model, ollama: ollama)
        }
        if let error = ollama.lastError { ErrorRow(message: error) }
    }

    @ViewBuilder private var cloudRows: some View {
        ProviderRow(selection: $model.llmProvider, providers: Self.cloudProviders)
        APIKeyRow(model: model, provider: provider)
        ControlRow {
            RowTitle(title: "Modelo",
                     detail: fetched.isEmpty ? nil : "\(fetched.count) modelos de texto na sua conta")
        } control: {
            ModelMenu(selection: $model.llmModel, options: modelOptions, loading: loading)
        }
        SelectedModelRow(id: model.llmModel, divider: error != nil)
        if let error { ErrorRow(message: error) }
    }

    @MainActor
    private func loadModels() async {
        loading = true
        defer { loading = false }
        do {
            fetched = try await LLM.availableModels(for: provider)
            error = nil
        } catch {
            fetched = []
            self.error = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }
}

private struct LocalModelRow: View {
    let item: LocalModel
    @ObservedObject var model: AppModel
    @ObservedObject var manager: ModelManager
    let divider: Bool

    private var isInstalled: Bool { manager.installed.contains(item.id) }
    private var isSelected: Bool { model.localModel == item.id }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                Image(systemName: isSelected && isInstalled ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 15))
                    .foregroundStyle(isSelected && isInstalled ? Theme.blue : Theme.secondary.opacity(0.5))
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.name).foregroundStyle(Theme.text)
                    Text("\(item.detail) · \(item.size)").font(.system(size: 12)).foregroundStyle(Theme.secondary)
                }
                Spacer()
                ScoreBars(accuracy: item.accuracy, speed: item.speed)
                    .padding(.trailing, 8)
                if let progress = manager.progress[item.id] {
                    ProgressView(value: progress).frame(width: 110)
                    Text(progress.formatted(.percent.precision(.fractionLength(0))))
                        .font(.system(size: 12).monospacedDigit())
                        .foregroundStyle(Theme.secondary)
                        .frame(width: 36)
                    Button { manager.cancel(item) } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain)
                        .foregroundStyle(Theme.secondary)
                } else if isInstalled {
                    if isSelected {
                        InUseBadge()
                    } else {
                        Button("Usar") { model.localModel = item.id }
                            .buttonStyle(SecondaryButtonStyle())
                    }
                    UninstallButton(name: item.name, size: item.size, inUse: isSelected) { model.uninstall(item) }
                } else {
                    Button { manager.download(item) } label: { Label("Baixar", systemImage: "arrow.down") }
                        .buttonStyle(SecondaryButtonStyle())
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            if divider { Divider().overlay(Theme.border).padding(.leading, 43) }
        }
    }
}

/// Price and intelligence × speed of the chosen cloud model.
private struct SelectedModelRow: View {
    let id: String
    let divider: Bool

    var body: some View {
        SettingRow(title: "Custo", detail: priceDetail, divider: divider) {
            if let rating = ModelCatalog.rating(for: id) {
                ScoreBars(accuracy: rating.intelligence, speed: rating.speed, quality: "inteligência")
            } else {
                Text("sem nota para este modelo").font(.system(size: 12)).foregroundStyle(Theme.secondary)
            }
        }
    }

    private var priceDetail: String {
        guard let price = ModelCatalog.price(for: id) else { return "Preço não cadastrado: o gasto não entra em Gastos" }
        func usd(_ value: Double) -> String { value.formatted(.currency(code: "USD").precision(.fractionLength(2))) }
        return "\(usd(price.input)) entrada · \(usd(price.output)) saída, por 1M tokens"
    }
}

/// Precision and speed as two short bars, so models can be compared at a glance.
private struct ScoreBars: View {
    let accuracy: Int
    let speed: Int
    /// "precisão" for transcription, "inteligência" for the AI models.
    var quality = "precisão"

    var body: some View {
        Grid(alignment: .trailing, horizontalSpacing: 8, verticalSpacing: 5) {
            row(quality, accuracy)
            row("velocidade", speed)
        }
        .help("\(quality.capitalized) \(accuracy)/100 · Velocidade \(speed)/100")
    }

    private func row(_ label: String, _ value: Int) -> some View {
        GridRow {
            Text(label).font(.system(size: 11)).foregroundStyle(Theme.secondary)
            Capsule()
                .fill(Theme.border)
                .frame(width: 64, height: 5)
                .overlay(alignment: .leading) {
                    Capsule().fill(Theme.blue).frame(width: 64 * CGFloat(value) / 100)
                }
        }
    }
}

private struct OllamaModelRow: View {
    let item: OllamaModel
    @ObservedObject var model: AppModel
    @ObservedObject var ollama: OllamaManager

    private var installed: Bool { ollama.isInstalled(item.id) }
    private var selected: Bool { model.llmModel == item.id }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                Image(systemName: selected && installed ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 15))
                    .foregroundStyle(selected && installed ? Theme.blue : Theme.secondary.opacity(0.5))
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.name).foregroundStyle(Theme.text)
                    Text("\(item.detail) · \(item.size)").font(.system(size: 12)).foregroundStyle(Theme.secondary)
                }
                Spacer()
                if let rating = ModelCatalog.rating(for: item.id) {
                    ScoreBars(accuracy: rating.intelligence, speed: rating.speed, quality: "inteligência")
                        .padding(.trailing, 8)
                }
                if let progress = ollama.progress[item.id] {
                    ProgressView(value: progress).frame(width: 110)
                    Text(progress.formatted(.percent.precision(.fractionLength(0))))
                        .font(.system(size: 12).monospacedDigit())
                        .foregroundStyle(Theme.secondary)
                        .frame(width: 36)
                    Button { ollama.cancel(item.id) } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain)
                        .foregroundStyle(Theme.secondary)
                } else if installed {
                    if selected {
                        InUseBadge()
                    } else {
                        Button("Usar") { model.llmModel = item.id }.buttonStyle(SecondaryButtonStyle())
                    }
                    UninstallButton(name: item.name, size: item.size, inUse: selected) {
                        Task { await model.uninstallOllama(item.id) }
                    }
                } else {
                    Button { ollama.pull(item.id) } label: { Label("Baixar", systemImage: "arrow.down") }
                        .buttonStyle(SecondaryButtonStyle())
                        .disabled(!ollama.isRunning)
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            Divider().overlay(Theme.border).padding(.leading, 43)
        }
    }
}

/// A model already in Ollama that isn't in the catalog (e.g. pulled from the Terminal).
private struct InstalledOllamaRow: View {
    let name: String
    @ObservedObject var model: AppModel
    @ObservedObject var ollama: OllamaManager

    var body: some View {
        SettingRow(title: name, detail: "Já instalado no Ollama") {
            let inUse = model.llmModel == name || model.llmModel + ":latest" == name
            if inUse {
                InUseBadge()
            } else {
                Button("Usar") { model.llmModel = name }.buttonStyle(SecondaryButtonStyle())
            }
            UninstallButton(name: name, size: nil, inUse: inUse) {
                Task { await model.uninstallOllama(name) }
            }
        }
    }
}

/// Trash button that asks before deleting a downloaded model from disk.
private struct UninstallButton: View {
    let name: String
    let size: String?
    let inUse: Bool
    let action: () -> Void
    @State private var confirming = false

    var body: some View {
        Button { confirming = true } label: { Image(systemName: "trash") }
            .buttonStyle(.plain)
            .foregroundStyle(Theme.secondary)
            .help("Desinstalar")
            .confirmationDialog("Desinstalar \(name)?", isPresented: $confirming) {
                Button("Desinstalar", role: .destructive, action: action)
            } message: {
                Text(message)
            }
    }

    private var message: String {
        var parts = [size.map { "Libera \($0) do disco." } ?? "Apaga o modelo do disco."]
        if inUse { parts.append("Ele está em uso: o Lidiflow passa para outro modelo baixado, se houver.") }
        parts.append("Dá para baixar de novo depois.")
        return parts.joined(separator: " ")
    }
}

private struct InUseBadge: View {
    var body: some View {
        Text("Em uso")
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(Theme.blue)
            .padding(.horizontal, 10)
            .padding(.vertical, 4)
            .background(Capsule().fill(Theme.blueTint))
    }
}

