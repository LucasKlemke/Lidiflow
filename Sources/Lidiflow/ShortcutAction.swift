import CoreGraphics
import Foundation

/// Things a global shortcut can do. Each action takes up to two shortcuts.
enum ShortcutAction: String, CaseIterable, Codable, Identifiable {
    case dictate, handsFree, command, pasteLast, copyLast, cancel

    static let maxShortcuts = 2

    var id: Self { self }

    var title: String {
        switch self {
        case .dictate: return "Ditar"
        case .handsFree: return "Mãos livres"
        case .command: return "Modo comando"
        case .pasteLast: return "Colar a última transcrição"
        case .copyLast: return "Copiar a última transcrição"
        case .cancel: return "Cancelar o ditado"
        }
    }

    var detail: String {
        switch self {
        case .dictate: return "Segure para falar, solte para colar"
        case .handsFree: return "Aperte para começar e de novo para parar"
        case .command: return "Segure com um texto selecionado e diga o que mudar"
        case .pasteLast, .copyLast: return "Funciona em qualquer app"
        case .cancel: return "Só vale enquanto estiver gravando"
        }
    }

    /// Held actions run while the shortcut is down; the rest fire once.
    var isHold: Bool { self == .dictate || self == .command }

    /// The original single-hotkey setting, used to seed defaults for existing installs.
    private static var legacyBase: (flag: CGEventFlags, key: Int64) {
        switch UserDefaults.standard.string(forKey: "hotkey") {
        case "rightOption": return (.maskAlternate, 61)
        case "rightCommand": return (.maskCommand, 54)
        default: return (.maskSecondaryFn, 63)
        }
    }

    static var defaults: [ShortcutAction: [KeyCombo]] {
        let base = legacyBase
        return [
            .dictate: [KeyCombo(modifiers: base.flag, modifierKey: base.key)],
            .handsFree: [KeyCombo(keyCode: 49, flags: base.flag, keyLabel: "Espaço")],
            .command: [KeyCombo(modifiers: base.flag.union(.maskControl))],
            .pasteLast: [KeyCombo(keyCode: 9, flags: [.maskControl, .maskCommand], keyLabel: "V")],
            .copyLast: [KeyCombo(keyCode: 8, flags: [.maskControl, .maskCommand], keyLabel: "C")],
            .cancel: [KeyCombo(keyCode: 53, flags: [], keyLabel: "Esc")],
        ]
    }
}
