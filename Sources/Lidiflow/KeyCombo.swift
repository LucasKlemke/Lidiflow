import AppKit
import CoreGraphics

/// A global shortcut. One of three kinds:
/// - a key plus optional modifiers (⌃⌥T, F6, Fn + Espaço)
/// - modifiers alone, held or tapped (Fn, Shift dir., ⌃⌥)
/// - an extra mouse button plus optional modifiers (botão do meio, botões laterais)
struct KeyCombo: Codable, Hashable {
    let keyCode: Int64?
    let modifiers: UInt64
    let keyLabel: String
    /// For a single modifier alone: which physical key (left/right side).
    var modifierKey: Int64?
    /// Mouse button number (2 = middle, 3 = back, 4 = forward…).
    var mouseButton: Int64?

    static let relevantFlags: CGEventFlags = [.maskCommand, .maskControl, .maskAlternate, .maskShift, .maskSecondaryFn]

    /// Function, arrow and navigation keys report Fn on Mac keyboards even when it isn't held.
    private static let fnReportingKeys: Set<Int64> = [
        122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80, 90,
        123, 124, 125, 126, 115, 119, 116, 121, 117, 114,
    ]

    private static let functionKeys: [Int64: String] = [
        122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8", 101: "F9",
        109: "F10", 103: "F11", 111: "F12", 105: "F13", 107: "F14", 113: "F15", 106: "F16",
        64: "F17", 79: "F18", 80: "F19", 90: "F20",
    ]

    private static let specialLabels: [Int64: String] = [
        49: "Espaço", 36: "↩", 48: "⇥", 51: "⌫", 117: "⌦", 53: "Esc",
        123: "←", 124: "→", 125: "↓", 126: "↑", 115: "Home", 119: "End", 116: "PgUp", 121: "PgDn",
    ]

    /// Physical modifier keys and the flag each one sets.
    static let modifierKeys: [Int64: (flag: CGEventFlags, label: String)] = [
        55: (.maskCommand, "⌘ esq."), 54: (.maskCommand, "⌘ dir."),
        58: (.maskAlternate, "⌥ esq."), 61: (.maskAlternate, "⌥ dir."),
        59: (.maskControl, "⌃ esq."), 62: (.maskControl, "⌃ dir."),
        56: (.maskShift, "Shift esq."), 60: (.maskShift, "Shift dir."),
        63: (.maskSecondaryFn, "Fn"), 179: (.maskSecondaryFn, "Fn"),
    ]

    init(keyCode: Int64, flags: CGEventFlags, keyLabel: String) {
        self.keyCode = keyCode
        self.modifiers = Self.normalize(flags, keyCode: keyCode).rawValue
        self.keyLabel = keyLabel
    }

    /// Modifiers alone. `modifierKey` pins a single modifier to one side of the keyboard.
    init(modifiers flags: CGEventFlags, modifierKey: Int64? = nil) {
        self.keyCode = nil
        self.modifiers = flags.intersection(Self.relevantFlags).rawValue
        self.keyLabel = ""
        self.modifierKey = modifierKey
    }

    init(mouseButton: Int64, flags: CGEventFlags) {
        self.keyCode = nil
        self.modifiers = flags.intersection(Self.relevantFlags).rawValue
        self.keyLabel = ""
        self.mouseButton = mouseButton
    }

    /// Builds a key combo from a key event recorded in the app's own window.
    init?(event: NSEvent) {
        guard event.type == .keyDown else { return nil }
        let code = Int64(event.keyCode)
        let label = Self.functionKeys[code] ?? Self.specialLabels[code]
            ?? event.charactersIgnoringModifiers?.uppercased() ?? "?"
        self.init(keyCode: code, flags: Self.flags(from: event.modifierFlags), keyLabel: label)
    }

    static func flags(from modifierFlags: NSEvent.ModifierFlags) -> CGEventFlags {
        var flags = CGEventFlags()
        if modifierFlags.contains(.command) { flags.insert(.maskCommand) }
        if modifierFlags.contains(.control) { flags.insert(.maskControl) }
        if modifierFlags.contains(.option) { flags.insert(.maskAlternate) }
        if modifierFlags.contains(.shift) { flags.insert(.maskShift) }
        if modifierFlags.contains(.function) { flags.insert(.maskSecondaryFn) }
        return flags
    }

    var flags: CGEventFlags { CGEventFlags(rawValue: modifiers) }
    var isModifierOnly: Bool { keyCode == nil && mouseButton == nil }

    /// Key cap labels in macOS order, e.g. ["Fn", "⌃", "⌥", "T"].
    var keys: [String] {
        if isModifierOnly, let modifierKey, let side = Self.modifierKeys[modifierKey] {
            return [side.label]
        }
        var keys: [String] = []
        if flags.contains(.maskSecondaryFn) { keys.append("Fn") }
        if flags.contains(.maskControl) { keys.append("⌃") }
        if flags.contains(.maskAlternate) { keys.append("⌥") }
        if flags.contains(.maskShift) { keys.append("Shift") }
        if flags.contains(.maskCommand) { keys.append("⌘") }
        if let mouseButton { keys.append(Self.mouseLabel(mouseButton)) }
        if keyCode != nil { keys.append(keyLabel) }
        return keys
    }

    static func mouseLabel(_ button: Int64) -> String {
        switch button {
        case 2: return "Mouse meio"
        case 3: return "Mouse voltar"
        case 4: return "Mouse avançar"
        default: return "Mouse \(button + 1)"
        }
    }

    /// Whether the combo can be a global shortcut without blocking normal typing.
    /// A plain key is only allowed for F-keys (or anything when `allowPlainKeys`).
    func isUsable(allowPlainKeys: Bool = false) -> Bool {
        if isModifierOnly { return !flags.isEmpty }
        if mouseButton != nil { return true }
        return !flags.isEmpty || allowPlainKeys || Self.functionKeys[keyCode ?? -1] != nil
    }

    func matches(keyCode: Int64, flags: CGEventFlags) -> Bool {
        guard let own = self.keyCode else { return false }
        return keyCode == own && Self.normalize(flags, keyCode: keyCode).rawValue == modifiers
    }

    func matches(mouseButton: Int64, flags: CGEventFlags) -> Bool {
        mouseButton == self.mouseButton && flags.intersection(Self.relevantFlags).rawValue == modifiers
    }

    /// Whether held modifiers `flags`, just changed by physical key `changedKey`, press this combo.
    func matchesModifiers(_ flags: CGEventFlags, changedKey: Int64) -> Bool {
        guard isModifierOnly, flags.intersection(Self.relevantFlags).rawValue == modifiers else { return false }
        guard let modifierKey else { return true }
        return changedKey == modifierKey || (Self.isFn(changedKey) && Self.isFn(modifierKey))
    }

    private static func isFn(_ key: Int64) -> Bool { key == 63 || key == 179 }

    private static func normalize(_ flags: CGEventFlags, keyCode: Int64) -> CGEventFlags {
        var flags = flags.intersection(relevantFlags)
        if fnReportingKeys.contains(keyCode) { flags.remove(.maskSecondaryFn) }
        return flags
    }

    // Identity ignores the display label.
    static func == (lhs: KeyCombo, rhs: KeyCombo) -> Bool {
        lhs.keyCode == rhs.keyCode && lhs.modifiers == rhs.modifiers
            && lhs.modifierKey == rhs.modifierKey && lhs.mouseButton == rhs.mouseButton
    }

    func hash(into hasher: inout Hasher) {
        hasher.combine(keyCode)
        hasher.combine(modifiers)
        hasher.combine(modifierKey)
        hasher.combine(mouseButton)
    }
}
