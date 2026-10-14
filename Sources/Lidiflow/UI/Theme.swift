import AppKit
import SwiftUI

/// LiderHub identity: the icon's blue on calm, warm-neutral surfaces.
enum Theme {
    static let blue = Color(hex: 0x1F6BFF)
    static let blueDeep = Color(hex: 0x0A47E8)
    static let blueGlow = Color(hex: 0x5AA2FF)
    static let blueTint = Color(hex: 0x1F6BFF).opacity(0.1)

    static let background = Color(light: 0xFAFAF8, dark: 0x121316)
    static let sidebar = Color(light: 0xF2F2EF, dark: 0x0C0D10)
    static let card = Color(light: 0xFFFFFF, dark: 0x1B1C20)
    static let border = Color(light: 0xE8E7E3, dark: 0x2A2B31)
    static let hover = Color(light: 0x000000, dark: 0xFFFFFF).opacity(0.05)
    static let text = Color(light: 0x1A1A1A, dark: 0xF2F2F3)
    static let secondary = Color(light: 0x6F6F6B, dark: 0x9C9CA3)

    static let heroGradient = LinearGradient(
        colors: [Color(hex: 0x3D8DFF), Color(hex: 0x1F6BFF), Color(hex: 0x0A47E8)],
        startPoint: .topLeading,
        endPoint: .bottomTrailing
    )

    /// Serif display face, the counterpart to Wispr's italic accents.
    static func display(_ size: CGFloat) -> Font {
        .system(size: size, weight: .regular, design: .serif)
    }
}

extension Color {
    init(hex: UInt32) {
        self.init(
            red: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255
        )
    }

    init(light: UInt32, dark: UInt32) {
        self.init(nsColor: NSColor(name: nil) { appearance in
            let hex = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
            return NSColor(
                red: CGFloat((hex >> 16) & 0xFF) / 255,
                green: CGFloat((hex >> 8) & 0xFF) / 255,
                blue: CGFloat(hex & 0xFF) / 255,
                alpha: 1
            )
        })
    }
}

// MARK: - Layout

/// Scrolling page with consistent margins and a readable max width.
struct Page<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 28) {
                content
            }
            .padding(.horizontal, 44)
            .padding(.top, 40)
            .padding(.bottom, 48)
            .frame(maxWidth: 820, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .background(Theme.background)
    }
}

/// Title with an optional italic serif accent, e.g. "Bem-vindo de volta, *Luke*".
struct PageHeader: View {
    let title: String
    var accent: String?
    var subtitle: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            (Text(title) + Text(accent.map { " \($0)" } ?? "").italic().foregroundColor(Theme.blue))
                .font(Theme.display(34))
                .foregroundStyle(Theme.text)
            if let subtitle {
                Text(subtitle)
                    .font(.system(size: 14))
                    .foregroundStyle(Theme.secondary)
            }
        }
    }
}

/// A titled group of rows on a card, used by the settings-style pages.
struct SectionCard<Content: View>: View {
    var title: String?
    var footer: String?
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let title {
                Text(title.uppercased())
                    .font(.system(size: 11, weight: .semibold))
                    .tracking(0.6)
                    .foregroundStyle(Theme.secondary)
                    .padding(.leading, 4)
            }
            VStack(alignment: .leading, spacing: 0) {
                content
            }
            .card(padding: 0)
            if let footer {
                Text(footer)
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.secondary)
                    .padding(.horizontal, 4)
            }
        }
    }
}

/// One row inside a `SectionCard`.
struct SettingRow<Trailing: View>: View {
    let title: String
    var detail: String?
    var divider = true
    @ViewBuilder let trailing: Trailing

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).foregroundStyle(Theme.text)
                    if let detail {
                        Text(detail).font(.system(size: 12)).foregroundStyle(Theme.secondary)
                    }
                }
                Spacer(minLength: 12)
                trailing
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            if divider { Divider().overlay(Theme.border).padding(.leading, 16) }
        }
    }
}

extension View {
    func card(padding: CGFloat = 16) -> some View {
        self
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12).fill(Theme.card))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Theme.border))
    }
}

// MARK: - Controls

struct KeyCaps: View {
    let keys: [String]
    var onBlue = false

    var body: some View {
        HStack(spacing: 4) {
            ForEach(keys, id: \.self) { key in
                Text(key)
                    .font(.system(size: 11, weight: .semibold, design: .rounded))
                    .padding(.horizontal, 7)
                    .padding(.vertical, 3)
                    .background(RoundedRectangle(cornerRadius: 6).fill(onBlue ? Color.white.opacity(0.18) : Theme.blueTint))
                    .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(onBlue ? Color.white.opacity(0.35) : Theme.blue.opacity(0.3)))
                    .foregroundStyle(onBlue ? Color.white : Theme.blue)
            }
        }
        .fixedSize()
    }
}

struct Chip: View {
    let title: String
    var systemImage: String?
    var keys: [String]?
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                if let systemImage { Image(systemName: systemImage).foregroundStyle(Theme.blue) }
                Text(title).foregroundStyle(Theme.text)
                if let keys { KeyCaps(keys: keys) }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .background(Capsule().fill(hovering ? Theme.blueTint : Theme.card))
            .overlay(Capsule().strokeBorder(hovering ? Theme.blue.opacity(0.4) : Theme.border))
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
    }
}

/// Filled blue pill, the primary action style.
struct PrimaryButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(.white)
            .padding(.horizontal, 14)
            .padding(.vertical, 7)
            .background(Capsule().fill(Theme.blue.opacity(configuration.isPressed ? 0.8 : 1)))
            .contentShape(Capsule())
    }
}

/// Subtle outlined pill for secondary actions.
struct SecondaryButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .medium))
            .foregroundStyle(Theme.text)
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            .background(Capsule().fill(configuration.isPressed ? Theme.hover : Theme.card))
            .overlay(Capsule().strokeBorder(Theme.border))
            .contentShape(Capsule())
    }
}

struct SearchField: View {
    let prompt: String
    @Binding var text: String

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(Theme.secondary)
            TextField(prompt, text: $text).textFieldStyle(.plain)
            if !text.isEmpty {
                Button { text = "" } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain)
                    .foregroundStyle(Theme.secondary)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(Capsule().fill(Theme.card))
        .overlay(Capsule().strokeBorder(Theme.border))
    }
}

/// Plain text field drawn inside cards.
struct InlineField: View {
    let prompt: String
    @Binding var text: String
    var onSubmit: () -> Void = {}

    var body: some View {
        TextField(prompt, text: $text)
            .textFieldStyle(.plain)
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(RoundedRectangle(cornerRadius: 8).fill(Theme.background))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Theme.border))
            .onSubmit(onSubmit)
    }
}

/// Wraps its children onto new lines when a row runs out of width.
struct FlowLayout: Layout {
    var spacing: CGFloat = 8

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(subviews, width: proposal.width ?? .infinity)
        let height = rows.map(\.height).reduce(0, +) + spacing * CGFloat(max(rows.count - 1, 0))
        let width = rows.map(\.width).max() ?? 0
        return CGSize(width: proposal.width ?? width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for row in arrange(subviews, width: bounds.width) {
            var x = bounds.minX
            for index in row.indices {
                let size = subviews[index].sizeThatFits(.unspecified)
                subviews[index].place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
            y += row.height + spacing
        }
    }

    private struct Row {
        var indices: [Int] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    private func arrange(_ subviews: Subviews, width: CGFloat) -> [Row] {
        var rows = [Row()]
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            if !rows[rows.count - 1].indices.isEmpty, rows[rows.count - 1].width + spacing + size.width > width {
                rows.append(Row())
            }
            var row = rows[rows.count - 1]
            row.width += (row.indices.isEmpty ? 0 : spacing) + size.width
            row.height = max(row.height, size.height)
            row.indices.append(index)
            rows[rows.count - 1] = row
        }
        return rows
    }
}

func copyToClipboard(_ text: String) {
    NSPasteboard.general.clearContents()
    NSPasteboard.general.setString(text, forType: .string)
}
