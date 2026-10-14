import AppKit
import SwiftUI

final class OverlayState: ObservableObject {
    enum Phase: Equatable {
        case hidden, listening, handsFree, command, transcribing, polishing, thinking
        case transforming(String)
        case error(String)
    }

    @Published var phase: Phase = .hidden
    @Published var level: Float = 0
}

/// Small floating pill at the bottom of the screen that never takes focus.
final class OverlayWindow {
    let state = OverlayState()
    private let panel: NSPanel

    init() {
        panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 260, height: 44),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true
        )
        panel.level = .statusBar
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.ignoresMouseEvents = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.contentView = NSHostingView(rootView: OverlayView(state: state))
    }

    func show(_ phase: OverlayState.Phase) {
        state.phase = phase
        if phase == .hidden {
            panel.orderOut(nil)
            return
        }
        if let screen = NSScreen.main {
            let frame = screen.visibleFrame
            panel.setFrameOrigin(NSPoint(x: frame.midX - panel.frame.width / 2, y: frame.minY + 24))
        }
        panel.orderFrontRegardless()
    }
}

private struct OverlayView: View {
    @ObservedObject var state: OverlayState

    var body: some View {
        HStack(spacing: 10) {
            indicator
            Text(label)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(.white)
                .lineLimit(1)
        }
        .padding(.horizontal, 16)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Capsule().fill(Color.black.opacity(0.82)))
    }

    @ViewBuilder private var indicator: some View {
        switch state.phase {
        case .listening, .handsFree, .command:
            HStack(spacing: 3) {
                ForEach(0..<5, id: \.self) { i in
                    Capsule()
                        .fill(state.phase == .command ? Color.orange : Color(red: 0.3, green: 0.58, blue: 1))
                        .frame(width: 3, height: barHeight(i))
                }
            }
            .frame(height: 22)
            .animation(.easeOut(duration: 0.08), value: state.level)
        case .error:
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.yellow)
        default:
            ProgressView().controlSize(.small).tint(.white)
        }
    }

    private var label: String {
        switch state.phase {
        case .listening: return "Ouvindo…"
        case .handsFree:
            let keys = (Settings.shortcuts[.handsFree]?.first ?? Settings.shortcuts[.dictate]?.first)?.keys ?? []
            return keys.isEmpty ? "Mãos livres" : "Mãos livres · \(keys.joined(separator: " ")) para parar"
        case .command: return "Diga o que fazer…"
        case .thinking: return "Pensando…"
        case .transcribing: return "Transcrevendo…"
        case .polishing: return "Ajustando texto…"
        case .transforming(let name): return "✨ \(name)…"
        case .error(let message): return message
        case .hidden: return ""
        }
    }

    private func barHeight(_ index: Int) -> CGFloat {
        let weights: [CGFloat] = [0.5, 0.8, 1, 0.8, 0.5]
        let level = min(CGFloat(state.level) * 12, 1)
        return 4 + 18 * level * weights[index]
    }
}
