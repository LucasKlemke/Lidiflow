import AppKit

/// Reads the selection from, and pastes text into, the focused app.
enum TextInserter {
    private static let vKey: CGKeyCode = 9
    private static let cKey: CGKeyCode = 8

    /// Pastes text, then restores the previous clipboard.
    static func insert(_ text: String) {
        let pasteboard = NSPasteboard.general
        let saved = snapshot(pasteboard)

        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        let ourChange = pasteboard.changeCount
        postCommand(vKey)

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) {
            // Leave the clipboard alone if something else wrote to it meanwhile.
            guard pasteboard.changeCount == ourChange else { return }
            restore(saved, to: pasteboard)
        }
    }

    enum Target { case editable, notEditable, unknown }

    /// Roles that take typed text.
    private static let textRoles: Set<String> = ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField"]
    /// Roles that certainly do not: pasting there does nothing, or something unwanted (a Finder list renames or
    /// duplicates files). Ambiguous containers such as AXGroup and AXWebArea are left out on purpose: Electron and
    /// web apps often report those for a real text box.
    private static let nonTextRoles: Set<String> = [
        "AXList", "AXOutline", "AXTable", "AXBrowser", "AXButton", "AXImage", "AXStaticText", "AXLink",
        "AXCheckBox", "AXRadioButton", "AXSlider", "AXMenu", "AXMenuBar", "AXMenuItem", "AXToolbar", "AXSplitter",
        "AXScrollArea", "AXPopUpButton", "AXDisclosureTriangle",
    ]

    /// Whether the focused element takes text, so a dictation is pasted only where it lands somewhere.
    /// When it cannot tell, it says so, and the caller pastes as before rather than lose a dictation.
    static func focusedTarget() -> Target {
        let app = NSWorkspace.shared.frontmostApplication
        if let app {
            // Electron apps (Slack, Notion, VS Code) build their accessibility tree only when asked.
            AXUIElementSetAttributeValue(AXUIElementCreateApplication(app.processIdentifier),
                                         "AXManualAccessibility" as CFString, kCFBooleanTrue)
        }
        // The Finder only takes text in real fields (rename, search); anywhere else a paste copies files.
        let isFinder = app?.bundleIdentifier == "com.apple.finder"
        let system = AXUIElementCreateSystemWide()
        var focused: CFTypeRef?
        let status = AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused)
        // "Nothing focused" is also what Chrome reports for a focused text box while its accessibility is off.
        guard status == .success, let focused, CFGetTypeID(focused) == AXUIElementGetTypeID()
        else { return isFinder ? .notEditable : .unknown }
        let element = focused as! AXUIElement

        var roleValue: CFTypeRef?
        AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &roleValue)
        let role = roleValue as? String ?? ""
        if textRoles.contains(role) { return .editable }
        var settable = DarwinBoolean(false)
        if AXUIElementIsAttributeSettable(element, kAXValueAttribute as CFString, &settable) == .success, settable.boolValue,
           !nonTextRoles.contains(role) {
            return .editable
        }
        return nonTextRoles.contains(role) || isFinder ? .notEditable : .unknown
    }

    /// The focused app's selected text, via Accessibility or, failing that, a Cmd+C round trip.
    @MainActor
    static func selectedText() async -> String? {
        if let text = accessibilitySelection() { return text }

        let pasteboard = NSPasteboard.general
        let saved = snapshot(pasteboard)
        let before = pasteboard.changeCount
        postCommand(cKey)
        try? await Task.sleep(for: .milliseconds(180))
        guard pasteboard.changeCount != before else { return nil }
        let text = pasteboard.string(forType: .string)
        restore(saved, to: pasteboard)
        return text?.isEmpty == false ? text : nil
    }

    private static func accessibilitySelection() -> String? {
        let system = AXUIElementCreateSystemWide()
        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
              let focused, CFGetTypeID(focused) == AXUIElementGetTypeID() else { return nil }
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(focused as! AXUIElement, kAXSelectedTextAttribute as CFString, &value) == .success,
              let text = value as? String, !text.isEmpty else { return nil }
        return text
    }

    private static func postCommand(_ key: CGKeyCode) {
        let source = CGEventSource(stateID: .combinedSessionState)
        for keyDown in [true, false] {
            let event = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: keyDown)
            event?.flags = .maskCommand
            event?.post(tap: .cghidEventTap)
        }
    }

    private static func snapshot(_ pasteboard: NSPasteboard) -> [NSPasteboardItem] {
        pasteboard.pasteboardItems?.map { item in
            let copy = NSPasteboardItem()
            for type in item.types {
                if let data = item.data(forType: type) { copy.setData(data, forType: type) }
            }
            return copy
        } ?? []
    }

    private static func restore(_ items: [NSPasteboardItem], to pasteboard: NSPasteboard) {
        pasteboard.clearContents()
        if !items.isEmpty { pasteboard.writeObjects(items) }
    }
}
