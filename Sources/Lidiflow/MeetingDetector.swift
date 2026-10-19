import AppKit
import CoreAudio
import os

/// Notices a call: a meeting app using the microphone (see docs/reunioes.md, phase 2). Checked every 3 s; the call
/// ends after 8 s with the app off the microphone.
@MainActor
final class MeetingDetector {
    private struct App {
        let name: String
        let bundlePrefix: String
        var isBrowser = false
    }

    private static let apps: [App] = [
        App(name: "Zoom", bundlePrefix: "us.zoom"),
        App(name: "Microsoft Teams", bundlePrefix: "com.microsoft.teams"),
        App(name: "Webex", bundlePrefix: "com.cisco.webex"),
        App(name: "FaceTime", bundlePrefix: "com.apple.FaceTime"),
        App(name: "Slack", bundlePrefix: "com.tinyspeck.slackmacgap"),
        App(name: "Discord", bundlePrefix: "com.hnc.Discord"),
        App(name: "Chrome", bundlePrefix: "com.google.Chrome", isBrowser: true),
        App(name: "Safari", bundlePrefix: "com.apple.Safari", isBrowser: true),
        App(name: "Arc", bundlePrefix: "company.thebrowser", isBrowser: true),
        App(name: "Edge", bundlePrefix: "com.microsoft.edgemac", isBrowser: true),
        App(name: "Firefox", bundlePrefix: "org.mozilla.firefox", isBrowser: true),
        App(name: "Brave", bundlePrefix: "com.brave.Browser", isBrowser: true),
    ]

    /// A browser counts only with a call open in one of its windows; the word names the service. "Meet" goes last:
    /// Zoom and Teams titles often say "Meeting".
    private static let webServices: [(word: String, name: String)] = [
        ("meet.google.com", "Google Meet"), ("Microsoft Teams", "Microsoft Teams"), ("Teams", "Microsoft Teams"),
        ("Zoom", "Zoom"), ("Whereby", "Whereby"), ("Jitsi", "Jitsi"), ("Meet", "Google Meet"),
    ]

    var onCallStarted: (String) -> Void = { _ in }
    var onCallEnded: (String) -> Void = { _ in }
    var isEnabled: () -> Bool = { true }

    /// The call in progress, by the name shown to the person.
    private(set) var call: String?
    /// What was last logged, so the log shows changes only (`log stream --predicate 'subsystem == "com.lidiflow.app"'`).
    private var lastReport = ""
    private static let log = Logger(subsystem: "com.lidiflow.app", category: "meetings")
    private var lastSeen: Date?
    private var timer: Timer?

    func start() {
        timer = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.check() }
        }
    }

    private func check() {
        guard isEnabled() else {
            call = nil
            return
        }
        let now = Date()
        let found = Self.currentCall()
        if #available(macOS 14.2, *) {
            let report = "microfone: \(Self.microphoneUsers().filter { !$0.isEmpty }.sorted()) → chamada: \(found ?? "nenhuma")"
            if report != lastReport {
                lastReport = report
                Self.log.notice("\(report, privacy: .public)")
            }
        }
        if let found {
            lastSeen = now
            if call != found {
                call = found
                onCallStarted(found)
            }
        } else if let ongoing = call, let seen = lastSeen, now.timeIntervalSince(seen) >= 8 {
            call = nil
            onCallEnded(ongoing)
        }
    }

    /// The meeting app on the microphone right now, if any.
    private static func currentCall() -> String? {
        guard #available(macOS 14.2, *) else { return nil }
        let own = Bundle.main.bundleIdentifier ?? "com.lidiflow.app"
        let listening = microphoneUsers().filter { !$0.isEmpty && !$0.hasPrefix(own) }
        for app in apps where listening.contains(where: { $0.hasPrefix(app.bundlePrefix) }) {
            guard app.isBrowser else { return app.name }
            switch webService(in: app.bundlePrefix) {
            case .found(let service): return service
            // Some browsers (Chrome) show no windows to Accessibility until asked by an assistive app. A browser on
            // the microphone is most likely a call, and the offer is light and goes away by itself.
            case .unreadable: return app.name
            case .none: continue
            }
        }
        return nil
    }

    /// Bundle ids of the processes recording from any input right now.
    @available(macOS 14.2, *)
    private static func microphoneUsers() -> [String] {
        let system = AudioObjectID(kAudioObjectSystemObject)
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyProcessObjectList,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr else { return [] }
        var processes = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &processes) == noErr else { return [] }

        return processes.compactMap { process in
            var running: UInt32 = 0
            var runningSize = UInt32(MemoryLayout<UInt32>.size)
            var runningAddress = AudioObjectPropertyAddress(mSelector: kAudioProcessPropertyIsRunningInput,
                                                            mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            guard AudioObjectGetPropertyData(process, &runningAddress, 0, nil, &runningSize, &running) == noErr,
                  running != 0 else { return nil }
            var bundle: Unmanaged<CFString>?
            var bundleSize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
            var bundleAddress = AudioObjectPropertyAddress(mSelector: kAudioProcessPropertyBundleID,
                                                           mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            guard AudioObjectGetPropertyData(process, &bundleAddress, 0, nil, &bundleSize, &bundle) == noErr,
                  let bundle else { return nil }
            return bundle.takeRetainedValue() as String
        }
    }

    private enum WebService { case found(String), unreadable, none }

    /// The call service open in a browser, read from its window titles (the active tab of each window).
    private static func webService(in bundlePrefix: String) -> WebService {
        var readAny = false
        let browsers = NSWorkspace.shared.runningApplications.filter { $0.bundleIdentifier?.hasPrefix(bundlePrefix) == true }
        for browser in browsers {
            let app = AXUIElementCreateApplication(browser.processIdentifier)
            var windows: CFTypeRef?
            guard AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &windows) == .success,
                  let list = windows as? [AXUIElement] else { continue }
            for window in list {
                var title: CFTypeRef?
                AXUIElementCopyAttributeValue(window, kAXTitleAttribute as CFString, &title)
                guard let text = title as? String else { continue }
                readAny = true
                if let match = webServices.first(where: { text.range(of: $0.word, options: .caseInsensitive) != nil }) {
                    return .found(match.name)
                }
            }
        }
        return readAny ? .none : .unreadable
    }
}
