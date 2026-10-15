import SwiftUI

struct UsageView: View {
    @ObservedObject var log: UsageLog
    @State private var period = Period.month

    enum Period: String, CaseIterable, Identifiable {
        case today = "Hoje", week = "7 dias", month = "Este mês", all = "Tudo"

        var id: Self { self }

        var start: Date? {
            let calendar = Calendar.current
            let now = Date()
            switch self {
            case .today: return calendar.startOfDay(for: now)
            case .week: return calendar.date(byAdding: .day, value: -6, to: calendar.startOfDay(for: now))
            case .month: return calendar.dateInterval(of: .month, for: now)?.start
            case .all: return nil
            }
        }
    }

    private var filteredRecords: [UsageRecord] {
        guard let start = period.start else { return log.records }
        return log.records.filter { $0.date >= start }
    }

    var body: some View {
        // Filtered once per render and handed to each section.
        let records = filteredRecords
        return Page {
            HStack(alignment: .bottom) {
                PageHeader(title: "Gastos", accent: "sob controle",
                           subtitle: "Quanto o Lidiflow gastou nas suas chaves de API, pedido a pedido.")
                Spacer()
                Picker("", selection: $period) {
                    ForEach(Period.allCases) { Text($0.rawValue).tag($0) }
                }
                .labelsHidden()
                .pickerStyle(.segmented)
                .fixedSize()
            }

            if records.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "creditcard")
                        .font(.system(size: 26))
                        .foregroundStyle(Theme.blue)
                    Text("Nenhum gasto \(period == .all ? "registrado" : "no período")")
                        .font(Theme.display(20))
                        .foregroundStyle(Theme.text)
                    Text("Transcrições e IA na nuvem com a sua chave aparecem aqui. O que roda no Mac é grátis.")
                        .foregroundStyle(Theme.secondary)
                        .multilineTextAlignment(.center)
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 36)
                .card()
            } else {
                summary(records)
                byProvider(records)
                recent(records)
            }

            HStack {
                Text("Valores estimados com os preços públicos de cada modelo. O valor cobrado de fato está no painel do provedor.")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer()
                if !log.records.isEmpty {
                    Button("Zerar registro", action: log.clear)
                        .buttonStyle(SecondaryButtonStyle())
                }
            }
        }
    }

    // MARK: - Sections

    private func summary(_ records: [UsageRecord]) -> some View {
        let tokens = records.reduce(0) { $0 + $1.inputTokens + $1.outputTokens }
        let minutes = records.reduce(0) { $0 + $1.audioSeconds } / 60
        return HStack(spacing: 12) {
            SummaryTile(title: "Gasto estimado", value: Self.money(total(records)), highlighted: true)
            SummaryTile(title: "Pedidos", value: records.count.formatted())
            SummaryTile(title: "Tokens de IA", value: tokens.formatted(.number.notation(.compactName)))
            SummaryTile(title: "Áudio transcrito", value: Self.minutes(minutes))
        }
    }

    private func byProvider(_ records: [UsageRecord]) -> some View {
        let groups = Self.groupedByTotal(records, by: \.provider)
        return SectionCard(title: "Por provedor") {
            ForEach(Array(groups.enumerated()), id: \.element.key) { index, group in
                let (provider, items) = (group.key, group.items)
                let models = Self.groupedByTotal(items, by: \.model)
                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: 10) {
                        ProviderIcon(provider: provider, size: 22)
                        Text(provider.title).fontWeight(.semibold).foregroundStyle(Theme.text)
                        if let url = provider.usageURL {
                            Link(destination: url) {
                                Label("Painel oficial", systemImage: "arrow.up.right.square")
                                    .font(.system(size: 12))
                            }
                            .foregroundStyle(Theme.blue)
                        }
                        Spacer()
                        Text(Self.money(group.total))
                            .font(.system(size: 14, weight: .semibold))
                            .foregroundStyle(Theme.text)
                            .monospacedDigit()
                    }
                    .padding(.horizontal, 16)
                    .padding(.top, 12)
                    .padding(.bottom, 6)
                    ForEach(models, id: \.key) { entry in
                        let (model, modelItems) = (entry.key, entry.items)
                        HStack {
                            Text(model).font(.system(size: 12, design: .monospaced)).foregroundStyle(Theme.text)
                            Text(Self.detail(modelItems)).font(.system(size: 12)).foregroundStyle(Theme.secondary)
                            Spacer()
                            Text(Self.money(entry.total))
                                .font(.system(size: 12))
                                .foregroundStyle(Theme.secondary)
                                .monospacedDigit()
                        }
                        .padding(.leading, 48)
                        .padding(.trailing, 16)
                        .padding(.vertical, 3)
                    }
                    Spacer().frame(height: 10)
                    if index < groups.count - 1 { Divider().overlay(Theme.border) }
                }
            }
        }
    }

    private func recent(_ records: [UsageRecord]) -> some View {
        let items = Array(records.prefix(50))
        return SectionCard(title: "Pedidos recentes",
                           footer: records.count > items.count ? "Mostrando os 50 mais recentes." : nil) {
            ForEach(items) { record in
                VStack(spacing: 0) {
                    HStack(spacing: 10) {
                        Image(systemName: record.kind == .transcription ? "waveform" : "sparkles")
                            .font(.system(size: 12))
                            .foregroundStyle(Theme.blue)
                            .frame(width: 18)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(record.kind == .transcription ? "Transcrição" : "IA")
                                .foregroundStyle(Theme.text)
                            Text("\(record.provider.title) · \(record.model) · \(Self.detail([record]))")
                                .font(.system(size: 12))
                                .foregroundStyle(Theme.secondary)
                                .lineLimit(1)
                        }
                        Spacer()
                        VStack(alignment: .trailing, spacing: 2) {
                            Text(record.cost.map(Self.money) ?? "sem preço")
                                .foregroundStyle(record.cost == nil ? Theme.secondary : Theme.text)
                                .monospacedDigit()
                            Text(record.date.formatted(date: .abbreviated, time: .shortened))
                                .font(.system(size: 11))
                                .foregroundStyle(Theme.secondary)
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    if record.id != items.last?.id { Divider().overlay(Theme.border).padding(.leading, 44) }
                }
            }
        }
    }

    // MARK: - Formatting

    private func total(_ items: [UsageRecord]) -> Double {
        items.reduce(0) { $0 + ($1.cost ?? 0) }
    }

    /// Groups records and sorts the groups by spend, totalling each group once.
    private static func groupedByTotal<Key: Hashable>(_ records: [UsageRecord], by key: (UsageRecord) -> Key)
        -> [(key: Key, items: [UsageRecord], total: Double)] {
        Dictionary(grouping: records, by: key)
            .map { (key: $0.key, items: $0.value, total: $0.value.reduce(0) { $0 + ($1.cost ?? 0) }) }
            .sorted { $0.total > $1.total }
    }

    /// Cents matter here: a dictation costs a fraction of one.
    static func money(_ value: Double) -> String {
        let digits = value > 0 && value < 1 ? 4 : 2
        return value.formatted(.currency(code: "USD").precision(.fractionLength(digits)))
    }

    static func minutes(_ value: Double) -> String {
        value < 1 ? "\(Int((value * 60).rounded())) s" : "\(value.formatted(.number.precision(.fractionLength(0...1)))) min"
    }

    static func detail(_ items: [UsageRecord]) -> String {
        let tokens = items.reduce(0) { $0 + $1.inputTokens + $1.outputTokens }
        let seconds = items.reduce(0) { $0 + $1.audioSeconds }
        var parts: [String] = []
        if tokens > 0 { parts.append("\(tokens.formatted(.number.notation(.compactName))) tokens") }
        if seconds > 0 { parts.append(minutes(seconds / 60)) }
        if items.count > 1 { parts.append("\(items.count) pedidos") }
        return parts.joined(separator: " · ")
    }
}

private struct SummaryTile: View {
    let title: String
    let value: String
    var highlighted = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title.uppercased())
                .font(.system(size: 10, weight: .semibold))
                .tracking(0.6)
                .foregroundStyle(Theme.secondary)
            Text(value)
                .font(Theme.display(24))
                .foregroundStyle(highlighted ? Theme.blue : Theme.text)
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.7)
        }
        .card(padding: 14)
    }
}
