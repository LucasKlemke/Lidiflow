import AppKit
import SwiftUI

final class OverlayState: ObservableObject {
    enum Phase: Equatable {
        case hidden, listening, handsFree, command, transcribing, polishing, thinking
        case transforming(String)
        case error(String)
    }

    @Published var phase: Phase = .hidden
    /// Recent microphone levels for the waveform, oldest first.
    @Published var levels = [Float](repeating: 0, count: 12)
    /// Live transcript while recording; the pill shows its end and never grows.
    @Published var live = ""
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
        // Each recording starts flat instead of with the end of the last one.
        if [.listening, .handsFree].contains(phase), ![.listening, .handsFree, .command].contains(state.phase) {
            state.levels = state.levels.map { _ in 0 }
        }
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
            if isRecording && !state.live.isEmpty {
                Text(state.live)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(.white)
                    .lineLimit(1)
                    .truncationMode(.head)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .animation(nil, value: state.live)
            } else {
                Text(label)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(.white)
                    .lineLimit(1)
            }
        }
        .padding(.horizontal, 16)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Capsule().fill(Color.black.opacity(0.82)))
    }

    private var isRecording: Bool {
        [.listening, .handsFree, .command].contains(state.phase)
    }

    @ViewBuilder private var indicator: some View {
        switch state.phase {
        case .listening, .handsFree, .command:
            Waveform(levels: state.levels,
                     color: state.phase == .command ? .orange : Color(red: 0.3, green: 0.58, blue: 1))
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
}
