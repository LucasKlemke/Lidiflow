import SwiftUI

struct HomeView: View {
    @ObservedObject var model: AppModel
    var showHistory: () -> Void
    var showModels: () -> Void

    private var firstName: String {
        NSFullUserName().split(separator: " ").first.map(String.init) ?? ""
    }

    var body: some View {
        Page {
            HStack(alignment: .bottom) {
                PageHeader(title: firstName.isEmpty ? "Bem-vindo de volta" : "Bem-vindo de volta,", accent: firstName.isEmpty ? nil : firstName)
                Spacer()
                HStack(spacing: 8) {
                    StatPill(icon: "flame.fill", value: "\(model.streakDays)", label: model.streakDays == 1 ? "dia" : "dias")
                    StatPill(icon: "text.word.spacing", value: model.totalWords.formatted(.number.notation(.compactName)), label: "palavras")
                    StatPill(icon: "bolt.fill", value: "\(model.wordsPerMinute)", label: "ppm")
                }
            }

            Hero(model: model, showModels: showModels)

            if !model.accessibilityGranted || !model.microphoneGranted {
                PermissionsCard(model: model)
            }

            if model.history.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "waveform")
                        .font(.system(size: 26))
                        .foregroundStyle(Theme.blue)
                    Text("Seus ditados vão aparecer aqui")
                        .font(Theme.display(20))
                        .foregroundStyle(Theme.text)
                    Text("Segure \(model.dictateLabel) em qualquer app e comece a falar.")
                        .foregroundStyle(Theme.secondary)
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 36)
                .card()
            } else {
                VStack(alignment: .leading, spacing: 20) {
                    DictationTimeline(model: model, items: Array(model.history.prefix(30)))
                    if model.history.count > 30 {
                        Button("Ver histórico completo", action: showHistory)
                            .buttonStyle(SecondaryButtonStyle())
                    }
                }
            }
        }
    }
}

private struct Hero: View {
    @ObservedObject var model: AppModel
    var showModels: () -> Void

    var body: some View {
        HStack(alignment: .center, spacing: 24) {
            VStack(alignment: .leading, spacing: 14) {
                (Text("Segure ") + Text(model.dictateLabel).italic() + Text(" e fale"))
                    .font(Theme.display(30))
                    .foregroundStyle(.white)
                Text("Fale em qualquer app: o Lidiflow escreve, limpa e cola o texto onde o cursor estiver.")
                    .font(.system(size: 14))
                    .foregroundStyle(.white.opacity(0.85))
                    .fixedSize(horizontal: false, vertical: true)
                FlowLayout(spacing: 14) {
                    HeroHint(keys: model.keys(for: .handsFree), label: "Mãos livres")
                    HeroHint(keys: model.keys(for: .command), label: "Comando")
                    if let transform = model.shortcut(for: 0) {
                        HeroHint(keys: transform.keys, label: "Transforms")
                    }
                }
                HStack(spacing: 6) {
                    Circle().fill(model.isReady ? Color.green : Color.orange).frame(width: 7, height: 7)
                    Text(model.status).font(.system(size: 12, weight: .medium))
                    if !model.isReady {
                        Button("Abrir Modelos", action: showModels)
                            .buttonStyle(.plain)
                            .font(.system(size: 12, weight: .semibold))
                            .underline()
                    }
                }
                .foregroundStyle(.white)
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(Capsule().fill(.black.opacity(0.18)))
            }
            Spacer(minLength: 0)
            WaveMark()
                .frame(width: 150, height: 110)
        }
        .padding(28)
        .background(
            ZStack {
                Theme.heroGradient
                RadialGradient(colors: [Theme.blueGlow.opacity(0.7), .clear], center: .topTrailing, startRadius: 10, endRadius: 320)
            }
        )
        .clipShape(RoundedRectangle(cornerRadius: 16))
        .shadow(color: Theme.blue.opacity(0.25), radius: 18, y: 8)
    }
}

private struct HeroHint: View {
    let keys: [String]
    let label: String

    var body: some View {
        HStack(spacing: 6) {
            KeyCaps(keys: keys, onBlue: true)
            Text(label).font(.system(size: 12, weight: .medium)).foregroundStyle(.white.opacity(0.9))
        }
        .fixedSize()
    }
}

/// Glowing bars echoing the app icon.
private struct WaveMark: View {
    private let heights: [CGFloat] = [1, 0.55, 0.8, 0.45, 0.7, 1]

    var body: some View {
        HStack(alignment: .center, spacing: 10) {
            ForEach(heights.indices, id: \.self) { index in
                Capsule()
                    .fill(LinearGradient(colors: [.white, .white.opacity(0.75)], startPoint: .top, endPoint: .bottom))
                    .frame(width: 14, height: 110 * heights[index])
                    .shadow(color: .white.opacity(0.6), radius: 8)
            }
        }
    }
}

private struct StatPill: View {
    let icon: String
    let value: String
    let label: String

    var body: some View {
        HStack(spacing: 5) {
            Image(systemName: icon).font(.system(size: 11)).foregroundStyle(Theme.blue)
            Text(value).font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text)
            Text(label).font(.system(size: 12)).foregroundStyle(Theme.secondary)
        }
        .padding(.horizontal, 11)
        .padding(.vertical, 6)
        .background(Capsule().fill(Theme.card))
        .overlay(Capsule().strokeBorder(Theme.border))
        .fixedSize()
    }
}

struct PermissionsCard: View {
    @ObservedObject var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Label("Falta liberar algumas permissões", systemImage: "exclamationmark.shield.fill")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.orange)
                .padding(16)
            Divider().overlay(Theme.border)
            if !model.accessibilityGranted {
                PermissionRow(title: "Acessibilidade", detail: "Para detectar a tecla e colar o texto",
                              granted: false, divider: !model.microphoneGranted, action: model.openAccessibilitySettings)
            }
            if !model.microphoneGranted {
                PermissionRow(title: "Microfone", detail: "Para ouvir o que você dita",
                              granted: false, divider: false, action: model.requestMicrophone)
            }
        }
        .card(padding: 0)
    }
}

struct PermissionRow: View {
    let title: String
    let detail: String
    let granted: Bool
    var divider = true
    let action: () -> Void

    var body: some View {
        SettingRow(title: title, detail: detail, divider: divider) {
            if granted {
                Label("Liberado", systemImage: "checkmark.circle.fill")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(.green)
            } else {
                Button("Liberar", action: action).buttonStyle(PrimaryButtonStyle())
            }
        }
    }
}

// MARK: - History

struct HistoryView: View {
    @ObservedObject var model: AppModel
    @State private var query = ""

    private var filtered: [Dictation] {
        query.isEmpty ? model.history : model.history.filter { $0.text.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        Page {
            HStack(alignment: .bottom) {
                PageHeader(title: "Histórico", subtitle: "Tudo o que você ditou, guardado só neste Mac.")
                Spacer()
                if !model.history.isEmpty {
                    Button { model.clearHistory() } label: { Label("Limpar", systemImage: "trash") }
                        .buttonStyle(SecondaryButtonStyle())
                }
            }
            if model.history.isEmpty {
                Text("Nenhum ditado ainda. Segure \(model.dictateLabel) em qualquer app para começar.")
                    .foregroundStyle(Theme.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 40)
                    .card()
            } else {
                SearchField(prompt: "Buscar no histórico", text: $query)
                if filtered.isEmpty {
                    Text("Nada encontrado para \u{201C}\(query)\u{201D}.").foregroundStyle(Theme.secondary)
                } else {
                    DictationTimeline(model: model, items: filtered)
                }
            }
        }
    }
}

/// Dictations grouped by day (Hoje, Ontem, 12 de outubro…), like Wispr Flow's hub.
struct DictationTimeline: View {
    @ObservedObject var model: AppModel
    let items: [Dictation]

    private var days: [(title: String, items: [Dictation])] {
        let calendar = Calendar.current
        let grouped = Dictionary(grouping: items) { calendar.startOfDay(for: $0.date) }
        return grouped.keys.sorted(by: >).map { day in
            (Self.title(for: day), grouped[day]!.sorted { $0.date > $1.date })
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            ForEach(days, id: \.title) { day in
                VStack(alignment: .leading, spacing: 8) {
                    Text(day.title.uppercased())
                        .font(.system(size: 11, weight: .semibold))
                        .tracking(0.6)
                        .foregroundStyle(Theme.secondary)
                        .padding(.leading, 4)
                    VStack(spacing: 0) {
                        ForEach(day.items) { item in
                            DictationRow(item: item, divider: item.id != day.items.last?.id) {
                                model.delete(item)
                            }
                        }
                    }
                    .card(padding: 0)
                }
            }
        }
        // Balloons are drawn here, above every card, so no card edge or neighbor row can cut them.
        .overlayPreferenceValue(BalloonKey.self) { balloons in
            GeometryReader { proxy in
                ForEach(balloons, id: \.text) { balloon in
                    let button = proxy[balloon.anchor]
                    Balloon(text: balloon.text)
                        .position(x: button.midX, y: button.minY - 6 - 29 / 2)
                        .transition(.opacity)
                }
            }
            .allowsHitTesting(false)
        }
    }

    private static func title(for day: Date) -> String {
        let calendar = Calendar.current
        if calendar.isDateInToday(day) { return "Hoje" }
        if calendar.isDateInYesterday(day) { return "Ontem" }
        return day.formatted(.dateTime.day().month(.wide).locale(Locale(identifier: "pt_BR")))
    }
}

private struct DictationRow: View {
    let item: Dictation
    let divider: Bool
    let onDelete: () -> Void
    @State private var hovering = false
    @State private var copied = false

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .center, spacing: 16) {
                HStack(alignment: .firstTextBaseline, spacing: 16) {
                    Text(item.date, format: .dateTime.hour().minute())
                        .font(.system(size: 12).monospacedDigit())
                        .foregroundStyle(Theme.secondary)
                        .frame(width: 48, alignment: .leading)
                    Text(item.text)
                        .foregroundStyle(Theme.text)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                HStack(spacing: 4) {
                    RowAction(systemImage: copied ? "checkmark" : "doc.on.doc", help: copied ? "Copiado" : "Copiar",
                              tint: copied ? Theme.blue : nil) {
                        copyToClipboard(item.text)
                        withAnimation(.easeOut(duration: 0.15)) { copied = true }
                        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
                            withAnimation(.easeOut(duration: 0.15)) { copied = false }
                        }
                    }
                    RowAction(systemImage: "trash", help: "Apagar", destructive: true, action: onDelete)
                }
                .opacity(hovering || copied ? 1 : 0)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .background(Theme.hover.opacity(hovering ? 1 : 0))
            // The row being pointed at gets a blue mark on its edge, like a selection.
            .overlay(alignment: .leading) {
                Rectangle().fill(Theme.blue).frame(width: 2).opacity(hovering ? 1 : 0)
            }
            if divider { Divider().overlay(Theme.border).padding(.leading, 80) }
        }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
    }
}

/// A row's icon button: a framed square that lights up under the pointer (red for destructive ones).
private struct RowAction: View {
    let systemImage: String
    let help: String
    var tint: Color? = nil
    var destructive = false
    let action: () -> Void
    @State private var hovering = false
    @State private var showsBalloon = false

    var body: some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(tint ?? (hovering ? (destructive ? Color.red : Theme.text) : Theme.secondary))
                .frame(width: 28, height: 28)
                .background(RoundedRectangle(cornerRadius: 8)
                    .fill(hovering ? (destructive ? Color.red.opacity(0.1) : Theme.hover) : Theme.card))
                .overlay(RoundedRectangle(cornerRadius: 8)
                    .strokeBorder(hovering && destructive ? Color.red.opacity(0.4) : Theme.border))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(help)
        .anchorPreference(key: BalloonKey.self, value: .bounds) { showsBalloon ? [Balloon.Placed(text: help, anchor: $0)] : [] }
        .onHover { inside in
            hovering = inside
            if !inside { return withAnimation(.easeOut(duration: 0.1)) { showsBalloon = false } }
            // A short wait, so sweeping the pointer across the row does not flash balloons.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) {
                if hovering { withAnimation(.easeOut(duration: 0.15)) { showsBalloon = true } }
            }
        }
        .animation(.easeOut(duration: 0.12), value: hovering)
    }
}

/// A small dark label above a button, pointing at it.
private struct Balloon: View {
    struct Placed {
        let text: String
        let anchor: Anchor<CGRect>
    }

    let text: String

    var body: some View {
        VStack(spacing: 0) {
            Text(text)
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(Color.white)
                .fixedSize()
                .padding(.horizontal, 8)
                .frame(height: 24)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.black))
            Triangle().fill(Color.black).frame(width: 10, height: 5)
        }
        .shadow(color: .black.opacity(0.18), radius: 6, y: 2)
        .allowsHitTesting(false)
    }
}

/// The balloons shown in the timeline, with the button each one points at.
private struct BalloonKey: PreferenceKey {
    static let defaultValue: [Balloon.Placed] = []
    static func reduce(value: inout [Balloon.Placed], nextValue: () -> [Balloon.Placed]) { value += nextValue() }
}

private struct Triangle: Shape {
    func path(in rect: CGRect) -> Path {
        Path { path in
            path.move(to: CGPoint(x: rect.minX, y: rect.minY))
            path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY))
            path.addLine(to: CGPoint(x: rect.midX, y: rect.maxY))
            path.closeSubpath()
        }
    }
}
