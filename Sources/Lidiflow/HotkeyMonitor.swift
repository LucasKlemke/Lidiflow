import CoreGraphics
import Foundation

/// Global shortcuts from an active CGEventTap (requires Accessibility). Every action has up to
/// two user-defined shortcuts: a key combo, modifiers alone, or an extra mouse button.
///
/// Modifier-only holds arm for a moment before starting, so typing ⌘C or a capital letter
/// with a modifier-only shortcut doesn't start a dictation.
final class HotkeyMonitor {
    enum Action {
        case holdStart(ShortcutAction), holdEnd, switchToCommand
        case handsFree, stop, cancel, discard
        case pasteLast, copyLast
        case transform(Int)
    }

    /// What a modifier-only shortcut currently held is doing.
    private enum Hold {
        case none
        case armed(ShortcutAction, KeyCombo)
        case active(ShortcutAction, KeyCombo)
        /// Used by another shortcut; ignore until these modifiers are released.
        case consumed(CGEventFlags)
    }

    /// Key codes for 1…9, used by the positional transform shortcuts.
    static let digits: [Int64] = [18, 19, 20, 21, 23, 22, 26, 28, 25]
    private static let armingDelay: TimeInterval = 0.15

    var shortcuts: [ShortcutAction: [KeyCombo]] = ShortcutAction.defaults
    var onAction: (Action) -> Void = { _ in }
    var isRecording: () -> Bool = { false }
    var isHandsFree: () -> Bool = { false }
    /// While true (e.g. recording a shortcut in the app) every event passes through untouched.
    var isPaused: () -> Bool = { false }
    /// Index of the transform bound to a key event, given whether a dictation hold is down.
    var transformIndex: (_ keyCode: Int64, _ flags: CGEventFlags, _ holdDown: Bool) -> Int? = { _, _, _ in nil }

    private var tap: CFMachPort?
    private var hold = Hold.none
    private var arming: DispatchWorkItem?
    /// A non-hold action bound to modifiers alone fires when they're released untouched.
    private var modifierTap: (ShortcutAction, KeyCombo)?
    /// A hold started by a key or mouse button, released by its key-up / button-up.
    private var inputHold: (action: ShortcutAction, keyCode: Int64?, mouseButton: Int64?)?
    private var swallowedKeyUps: Set<Int64> = []
    private var swallowedMouseUps: Set<Int64> = []

    private var holdDown: Bool {
        switch hold {
        case .armed, .active: return true
        default: return inputHold != nil
        }
    }

    /// Returns false when the tap cannot be created (usually missing Accessibility permission).
    func start() -> Bool {
        guard tap == nil else { return true }
        let types: [CGEventType] = [.flagsChanged, .keyDown, .keyUp, .otherMouseDown, .otherMouseUp]
        let mask = types.reduce(CGEventMask(0)) { $0 | (1 << $1.rawValue) }
        let refcon = Unmanaged.passUnretained(self).toOpaque()
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: mask,
            callback: { _, type, event, refcon in
                guard let refcon else { return Unmanaged.passUnretained(event) }
                let monitor = Unmanaged<HotkeyMonitor>.fromOpaque(refcon).takeUnretainedValue()
                return monitor.handle(type, event) ? Unmanaged.passUnretained(event) : nil
            },
            userInfo: refcon
        ) else { return false }

        self.tap = tap
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        return true
    }

    /// Returns true to let the event through, false to swallow it.
    private func handle(_ type: CGEventType, _ event: CGEvent) -> Bool {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            return true
        }
        if isPaused() { return true }

        let flags = event.flags.intersection(KeyCombo.relevantFlags)
        switch type {
        case .flagsChanged:
            modifiersChanged(flags, key: event.getIntegerValueField(.keyboardEventKeycode))
            return true
        case .keyDown:
            let code = event.getIntegerValueField(.keyboardEventKeycode)
            if event.getIntegerValueField(.keyboardEventAutorepeat) != 0 {
                return !swallowedKeyUps.contains(code)
            }
            return keyDown(code, flags)
        case .keyUp:
            let code = event.getIntegerValueField(.keyboardEventKeycode)
            if let held = inputHold, held.keyCode == code {
                inputHold = nil
                onAction(.holdEnd)
            }
            return swallowedKeyUps.remove(code) == nil
        case .otherMouseDown:
            return mouseDown(event.getIntegerValueField(.mouseEventButtonNumber), flags)
        case .otherMouseUp:
            let button = event.getIntegerValueField(.mouseEventButtonNumber)
            if let held = inputHold, held.mouseButton == button {
                inputHold = nil
                onAction(.holdEnd)
            }
            return swallowedMouseUps.remove(button) == nil
        default:
            return true
        }
    }

    // MARK: - Modifiers alone

    private func modifiersChanged(_ flags: CGEventFlags, key: Int64) {
        switch hold {
        case .armed(let action, let combo), .active(let action, let combo):
            if !flags.contains(combo.flags) {
                releaseModifierHold()
            } else if flags != combo.flags, action == .dictate,
                      let command = modifierCombo(for: .command, flags, key) {
                // Adding a modifier to the dictation hold turns it into command mode.
                if case .active = hold {
                    hold = .active(.command, command)
                    onAction(.switchToCommand)
                } else {
                    hold = .armed(.command, command)
                }
            }
            return
        case .consumed(let used):
            if !flags.contains(used) { hold = .none }
            return
        case .none:
            break
        }

        if let (action, combo) = modifierTap {
            if !flags.contains(combo.flags) {
                modifierTap = nil
                fire(action)
            } else if flags != combo.flags {
                modifierTap = nil
            }
            return
        }
        guard !flags.isEmpty else { return }

        for action in [ShortcutAction.command, .dictate] {
            if let combo = modifierCombo(for: action, flags, key) {
                pressHold(action, combo)
                return
            }
        }
        for action in [ShortcutAction.handsFree, .pasteLast, .copyLast, .cancel] {
            if let combo = modifierCombo(for: action, flags, key) {
                modifierTap = (action, combo)
                return
            }
        }
    }

    private func modifierCombo(for action: ShortcutAction, _ flags: CGEventFlags, _ key: Int64) -> KeyCombo? {
        shortcuts[action, default: []].first { $0.matchesModifiers(flags, changedKey: key) }
    }

    private func pressHold(_ action: ShortcutAction, _ combo: KeyCombo) {
        if action == .dictate, isHandsFree() {
            hold = .consumed(combo.flags)
            onAction(.stop)
            return
        }
        hold = .armed(action, combo)
        let work = DispatchWorkItem { [weak self] in
            guard let self, case .armed(let action, let combo) = self.hold else { return }
            self.hold = .active(action, combo)
            self.onAction(.holdStart(action))
        }
        arming = work
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.armingDelay, execute: work)
    }

    private func releaseModifierHold() {
        if case .active = hold { onAction(.holdEnd) }
        arming?.cancel()
        hold = .none
    }

    /// Ends a modifier hold because another shortcut used it.
    private func consumeModifierHold(discarding: Bool) {
        switch hold {
        case .armed(_, let combo):
            arming?.cancel()
            hold = .consumed(combo.flags)
        case .active(_, let combo):
            if discarding { onAction(.discard) }
            hold = .consumed(combo.flags)
        default:
            break
        }
    }

    // MARK: - Keys and mouse buttons

    private func keyDown(_ code: Int64, _ flags: CGEventFlags) -> Bool {
        modifierTap = nil
        let match = ShortcutAction.allCases.first { action in
            shortcuts[action, default: []].contains { $0.matches(keyCode: code, flags: flags) }
        }
        if let action = match, trigger(action, keyCode: code, mouseButton: nil) {
            swallowedKeyUps.insert(code)
            return false
        }
        if let index = transformIndex(code, flags, holdDown) {
            consumeModifierHold(discarding: !isHandsFree())
            swallowedKeyUps.insert(code)
            onAction(.transform(index))
            return false
        }
        // The held modifiers belong to some other shortcut (⌘C, Fn + →…): drop the dictation.
        consumeModifierHold(discarding: true)
        return true
    }

    private func mouseDown(_ button: Int64, _ flags: CGEventFlags) -> Bool {
        modifierTap = nil
        let match = ShortcutAction.allCases.first { action in
            shortcuts[action, default: []].contains { $0.matches(mouseButton: button, flags: flags) }
        }
        guard let action = match, trigger(action, keyCode: nil, mouseButton: button) else { return true }
        swallowedMouseUps.insert(button)
        return false
    }

    /// Runs a shortcut pressed with a key or mouse button. Returns false to let the input through.
    private func trigger(_ action: ShortcutAction, keyCode: Int64?, mouseButton: Int64?) -> Bool {
        switch action {
        case .cancel:
            guard isRecording() || holdDown else { return false }
            consumeModifierHold(discarding: false)
            onAction(.cancel)
        case .handsFree:
            // Pressed during a dictation hold, it locks that dictation hands-free.
            if case .active(.dictate, _) = hold {
                consumeModifierHold(discarding: false)
            } else {
                consumeModifierHold(discarding: true)
            }
            onAction(.handsFree)
        case .dictate, .command:
            consumeModifierHold(discarding: true)
            if action == .dictate, isHandsFree() {
                onAction(.stop)
            } else {
                inputHold = (action, keyCode, mouseButton)
                onAction(.holdStart(action))
            }
        case .pasteLast, .copyLast:
            consumeModifierHold(discarding: true)
            fire(action)
        }
        return true
    }

    private func fire(_ action: ShortcutAction) {
        switch action {
        case .handsFree: onAction(.handsFree)
        case .pasteLast: onAction(.pasteLast)
        case .copyLast: onAction(.copyLast)
        case .cancel: if isRecording() { onAction(.cancel) }
        case .dictate, .command: break
        }
    }
}
