import SwiftUI

/// An action's shortcut slots in Ajustes: up to two, each recorded freely.
struct ActionShortcutsRow: View {
    @ObservedObject var model: AppModel
    let action: ShortcutAction
    let divider: Bool

    @StateObject private var capture = ShortcutCapture()
    @State private var recordingSlot: Int?

    private var combos: [KeyCombo] { model.shortcuts[action] ?? [] }

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 8) {
                HStack(alignment: .center, spacing: 12) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(action.title).foregroundStyle(Theme.text)
                        Text(action.detail).font(.system(size: 12)).foregroundStyle(Theme.secondary)
                    }
                    Spacer(minLength: 12)
                    HStack(spacing: 6) {
                        ForEach(0..<ShortcutAction.maxShortcuts, id: \.self) { slot in
                            slotView(slot)
                        }
                    }
                }
                if let message = capture.message, capture.isRecording {
                    Label(message, systemImage: "exclamationmark.triangle.fill")
                        .font(.system(size: 12))
                        .foregroundStyle(.orange)
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            if divider { Divider().overlay(Theme.border).padding(.leading, 16) }
        }
        .onDisappear { capture.stop() }
    }

    @ViewBuilder
    private func slotView(_ slot: Int) -> some View {
        if recordingSlot == slot, capture.isRecording {
            Button { stopRecording() } label: {
                HStack(spacing: 6) {
                    Image(systemName: "record.circle")
                    Text("Pressione…")
                    Spacer(minLength: 0)
                }
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(Theme.blue)
                .slotStyle(highlighted: true)
            }
            .buttonStyle(.plain)
            .help("Tecla, combinação, modificador sozinho ou botão do mouse. Esc cancela.")
        } else if slot < combos.count {
            HStack(spacing: 6) {
                Button { record(slot) } label: { KeyCaps(keys: combos[slot].keys) }
                    .buttonStyle(.plain)
                    .help("Clique para gravar outro atalho")
                Spacer(minLength: 0)
                Button { remove(slot) } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain)
                    .foregroundStyle(Theme.secondary)
                    .help("Remover")
            }
            .slotStyle(highlighted: false)
        } else if slot == combos.count {
            Button { record(slot) } label: {
                HStack(spacing: 6) {
                    Image(systemName: "plus")
                    Text("Adicionar")
                    Spacer(minLength: 0)
                }
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(Theme.secondary)
                .slotStyle(highlighted: false, dashed: true)
            }
            .buttonStyle(.plain)
        } else {
            Color.clear.frame(width: Self.slotWidth, height: 1)
        }
    }

    static let slotWidth: CGFloat = 168

    private func record(_ slot: Int) {
        recordingSlot = slot
        capture.start(
            model: model,
            options: .init(allowModifierOnly: true, allowPlainKeys: action == .cancel),
            validate: { combo in
                if slot < combos.count, combos[slot] == combo { return nil }
                return model.conflict(for: combo, in: .action(action))
            },
            onCapture: { combo in
                var list = combos
                if slot < list.count { list[slot] = combo } else { list.append(combo) }
                model.shortcuts[action] = list
                recordingSlot = nil
            }
        )
    }

    private func stopRecording() {
        capture.stop()
        recordingSlot = nil
    }

    private func remove(_ slot: Int) {
        var list = combos
        list.remove(at: slot)
        model.shortcuts[action] = list
    }
}

private extension View {
    func slotStyle(highlighted: Bool, dashed: Bool = false) -> some View {
        self
            .padding(.horizontal, 8)
            .frame(width: ActionShortcutsRow.slotWidth, height: 30)
            .background(RoundedRectangle(cornerRadius: 8).fill(highlighted ? Theme.blueTint : Theme.background))
            .overlay(
                RoundedRectangle(cornerRadius: 8)
                    .strokeBorder(highlighted ? Theme.blue : Theme.border,
                                  style: StrokeStyle(lineWidth: 1, dash: dashed ? [4, 3] : []))
            )
    }
}
