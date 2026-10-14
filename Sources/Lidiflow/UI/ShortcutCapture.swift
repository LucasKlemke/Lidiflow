import AppKit
import CoreGraphics

/// Records the next shortcut pressed in the app's window: a key combo, modifiers alone
/// (pressed and released with no other key) or an extra mouse button.
final class ShortcutCapture: ObservableObject {
    @Published private(set) var isRecording = false
    @Published var message: String?

    private var monitor: Any?
    private weak var model: AppModel?
    private var peak = CGEventFlags()
    private var singleModifierKey: Int64?
    private var keyPressed = false

    struct Options {
        var allowModifierOnly = true
        var allowPlainKeys = false
    }

    func start(model: AppModel, options: Options, validate: @escaping (KeyCombo) -> String?,
               onCapture: @escaping (KeyCombo) -> Void) {
        stop()
        self.model = model
        model.isRecordingShortcut = true
        isRecording = true
        message = nil
        reset()

        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged, .otherMouseDown]) { [weak self] event in
            guard let self else { return event }
            var combo: KeyCombo?
            switch event.type {
            case .keyDown:
                keyPressed = true
                let modifiers = KeyCombo.flags(from: event.modifierFlags)
                if event.keyCode == 53, modifiers.isEmpty, !options.allowPlainKeys {
                    stop()
                    return nil
                }
                combo = KeyCombo(event: event)
            case .flagsChanged:
                let flags = KeyCombo.flags(from: event.modifierFlags)
                if flags.isEmpty {
                    if !peak.isEmpty, !keyPressed, options.allowModifierOnly {
                        combo = KeyCombo(modifiers: peak, modifierKey: Self.isSingle(peak) ? singleModifierKey : nil)
                    }
                    reset()
                } else {
                    if peak.isEmpty { singleModifierKey = Int64(event.keyCode) }
                    peak.formUnion(flags)
                }
            case .otherMouseDown:
                combo = KeyCombo(mouseButton: Int64(event.buttonNumber), flags: KeyCombo.flags(from: event.modifierFlags))
            default:
                break
            }
            guard let combo else { return event.type == .flagsChanged ? event : nil }
            if let problem = validate(combo) {
                message = problem
            } else {
                onCapture(combo)
                stop()
            }
            return nil
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        if isRecording { model?.isRecordingShortcut = false }
        isRecording = false
    }

    private func reset() {
        peak = []
        singleModifierKey = nil
        keyPressed = false
    }

    private static func isSingle(_ flags: CGEventFlags) -> Bool {
        [CGEventFlags.maskCommand, .maskControl, .maskAlternate, .maskShift, .maskSecondaryFn]
            .filter { flags.contains($0) }.count == 1
    }

    deinit {
        if let monitor { NSEvent.removeMonitor(monitor) }
    }
}
