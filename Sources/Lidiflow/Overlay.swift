import AppKit
import Combine
import SwiftUI

/// Where the bar sits: like a dock, it snaps to one of three places on the screen with the mouse.
enum DockEdge: String, CaseIterable, Identifiable {
    case bottom, left, right

    var id: Self { self }
    var title: String {
        switch self {
        case .bottom: "Embaixo"
        case .left: "Esquerda"
        case .right: "Direita"
        }
    }
}

final class OverlayState: ObservableObject {
    enum Phase: Equatable {
        case hidden, listening, handsFree, command, transcribing, polishing, thinking
        case transforming(String)
        case error(String)
        /// A dictation with nowhere to paste it: shown with a button to copy it.
        case unplaced(String)
    }

    @Published var phase: Phase = .hidden
    /// Recent microphone levels for the waveform, oldest first.
    @Published var levels = [Float](repeating: 0, count: 12)
    /// Live transcript while recording; the pill shows its end and never grows.
    @Published var live = ""
    @Published var hovering = false
    /// The side bar's button under the pointer, named in a hint beside it.
    @Published var sideHint: SideHint?

    enum SideHint { case dictate, meeting }
    /// The dictation shortcut, shown when the idle bar is hovered.
    @Published var dictateKeys: [String] = []
    /// The shortcut that pastes the last dictation, for the "nowhere to paste" card.
    @Published var pasteKeys: [String] = []
    /// The microphone setting, for the bar's menu (a device UID, nil for the system default).
    @Published var microphone = Settings.microphone
    /// The connected microphones, for the same menu; set by AppController.
    var microphones: MicrophoneList?
    @Published var edge = Settings.dictationBarEdge
    @Published var dragging = false
    /// When the meeting being recorded started; the idle bar then shows the time and a stop button.
    @Published var meetingSince: Date?
    /// A call the Lidiflow noticed: an offer to record it, or to stop when it ended.
    @Published var offer: MeetingOffer?
    /// The offer arrives compact ("Reunião detectada • agora") and then opens into the card with the button.
    @Published var offerExpanded = true
    /// A recording just stopped with almost nothing said: "Começou sem querer?".
    @Published var mistake = false
    /// The live transcript panel, and what it shows.
    @Published var liveVisible = false
    @Published var meetingLive: [Meeting.Segment] = []

    var isRecording: Bool { [.listening, .handsFree, .command].contains(phase) }

    // Wired by AppController.
    var onDictate: () -> Void = {}
    var onStop: () -> Void = {}
    var onCancel: () -> Void = {}
    var onPasteLast: () -> Void = {}
    var onCopyUnplaced: () -> Void = {}
    var onDismissUnplaced: () -> Void = {}
    var onOpenApp: () -> Void = {}
    var onHideForAnHour: () -> Void = {}
    var onSelectMicrophone: (String?) -> Void = { _ in }
    var onToggleMeeting: () -> Void = {}
    var onStartMeeting: () -> Void = {}
    var onToggleLive: () -> Void = {}
    var onResolveMistake: (Bool) -> Void = { _ in }
    var onAcceptOffer: (MeetingOffer) -> Void = { _ in }
    var onDismissOffer: () -> Void = {}
    // Wired by OverlayWindow, which owns the panel.
    var onDragChanged: () -> Void = {}
    var onDragEnded: () -> Void = {}
    var onSelectEdge: (DockEdge) -> Void = { _ in }
}

/// The dictation bar, docked at the bottom or on a side of the screen. Idle, it is a small handle that grows on hover into a
/// "Ditar" button; while dictating it shows the waveform with cancel and finish buttons; then the progress.
/// It is a non-activating panel, so clicking it never takes focus from the app the text goes to.
final class OverlayWindow {
    let state = OverlayState()
    private let panel: NSPanel
    private let guide = DockGuideWindow()
    private var hiddenUntil: Date?
    /// Where the mouse grabbed the bar, while it is being dragged.
    private var dragOffset: NSPoint?
    /// Off in Settings, the bar only appears while dictating, as the indicator.
    var showsIdleBar = Settings.showsDictationBar {
        didSet { layout() }
    }

    private enum Size {
        static let idle = NSSize(width: 44, height: 8)
        static let idleSide = NSSize(width: 8, height: 44)
        static let hovered = NSSize(width: 204, height: 36)
        static let recording = NSSize(width: 300, height: 40)
        static let busy = NSSize(width: 260, height: 40)
        static let unplaced = NSSize(width: 380, height: 124)
        // On a side the whole bar stands up.
        // Just the two buttons; what dictates shows in a hint beside it (HintPanel).
        static let hoveredSide = NSSize(width: 40, height: 70)
        static let recordingSide = NSSize(width: 40, height: 104)
        static let busySide = NSSize(width: 40, height: 40)
        static let meeting = NSSize(width: 112, height: 28)
        static let offer = NSSize(width: 380, height: 64)
        static let offerCompact = NSSize(width: 240, height: 40)
        static let mistake = NSSize(width: 380, height: 64)
        static let meetingHover = NSSize(width: 220, height: 36)
        static let meetingHoverSide = NSSize(width: 40, height: 156)
        static let meetingSide = NSSize(width: 32, height: 84)
    }

    init() {
        panel = BarPanel(
            contentRect: NSRect(origin: .zero, size: Size.idle),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true
        )
        panel.level = .statusBar
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.contentView = FirstClickHostingView(rootView: BarView(state: state))
        state.onHideForAnHour = { [weak self] in self?.hide(for: 3600) }
        state.onDragChanged = { [weak self] in self?.dragChanged() }
        state.onDragEnded = { [weak self] in self?.dragEnded() }
        state.onSelectEdge = { [weak self] in self?.dock(at: $0) }
        // Hover changes the size; layout is deferred so SwiftUI finishes the current update first.
        hintObservation = state.$sideHint.removeDuplicates().dropFirst().sink { [weak self] _ in
            DispatchQueue.main.async { self?.layoutHint() }
        }
        hoverObservation = state.$hovering.dropFirst().sink { [weak self] _ in
            // Not animated: a bar resizing under the pointer makes AppKit report it leaving and coming back, over and over.
            DispatchQueue.main.async { self?.layout() }
        }
        offerObservation = state.$offer.removeDuplicates().dropFirst().sink { [weak self] offer in
            DispatchQueue.main.async { self?.offerChanged(offer) }
        }
        livePanel.state = state
        liveObservation = state.$liveVisible.combineLatest(state.$meetingSince).dropFirst().sink { [weak self] _ in
            DispatchQueue.main.async { self?.layoutLive() }
        }
        // With several monitors the bar sits on the one with the mouse, and moves when the mouse does, recording or not.
        screenTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            guard let self, self.dragOffset == nil, Self.mouseScreen != self.currentScreen else { return }
            self.layout()
        }
    }

    private static var mouseScreen: NSScreen? {
        let mouse = NSEvent.mouseLocation
        return NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main
    }

    private var hoverObservation: Any?
    private var hintObservation: Any?
    private var offerObservation: Any?
    private var liveObservation: Any?
    private let livePanel = LivePanel()
    private let hintPanel = HintPanel()
    private var screenTimer: Timer?
    private var currentScreen: NSScreen?

    func show(_ phase: OverlayState.Phase) {
        // Each recording starts flat instead of with the end of the last one.
        if [.listening, .handsFree].contains(phase), ![.listening, .handsFree, .command].contains(state.phase) {
            state.levels = state.levels.map { _ in 0 }
        }
        let changed = phase != state.phase
        state.phase = phase
        if phase != .hidden { state.hovering = false; state.sideHint = nil }
        layout(animated: changed)
    }

    /// Re-anchors the bar, e.g. after the screens change.
    func layout(animated: Bool = false) {
        guard dragOffset == nil else { return }
        let animates = animated && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        guard let size = currentSize else {
            hintPanel.hide()
            guard animates, panel.isVisible else { return panel.orderOut(nil) }
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.15
                panel.animator().alphaValue = 0
            } completionHandler: { [weak self] in
                guard let self, self.currentSize == nil else { return }
                self.panel.orderOut(nil)
                self.panel.alphaValue = 1
            }
            return
        }
        guard let screen = Self.mouseScreen else { return }
        currentScreen = screen
        let frame = NSRect(origin: Self.slot(state.edge, size: size, in: screen.visibleFrame), size: size)
        guard animates, frame != panel.frame else {
            panel.alphaValue = 1
            panel.setFrame(frame, display: true)
            panel.orderFrontRegardless()
            layoutLive()
            layoutHint()
            return
        }
        // The bar grows out of its handle (or out of nothing, when the idle bar is off), like a spring settling.
        if !panel.isVisible {
            let handle = state.edge == .bottom ? Size.idle : Size.idleSide
            panel.alphaValue = 0
            panel.setFrame(NSRect(origin: Self.slot(state.edge, size: handle, in: screen.visibleFrame), size: handle), display: false)
        }
        panel.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.28
            context.timingFunction = CAMediaTimingFunction(controlPoints: 0.2, 1.1, 0.3, 1)
            panel.animator().setFrame(frame, display: true)
            panel.animator().alphaValue = 1
        } completionHandler: { [weak self] in
            self?.layoutLive()
            self?.layoutHint()
        }
    }

    /// On a side, the hovered bar is only its buttons; the one under the pointer is named beside it, like a popover.
    private func layoutHint() {
        guard let hint = state.sideHint, state.phase == .hidden, state.hovering, state.edge != .bottom,
              state.meetingSince == nil, state.offer == nil, !state.mistake, dragOffset == nil, panel.isVisible else {
            hintPanel.hide()
            return
        }
        switch hint {
        // Centers of the two buttons: 6 pt padding + half of 26, then 6 pt spacing + another 26.
        case .dictate: hintPanel.show(beside: panel.frame, edge: state.edge, centerFromTop: 19, title: "Ditar", keys: state.dictateKeys)
        case .meeting: hintPanel.show(beside: panel.frame, edge: state.edge, centerFromTop: 51, title: "Gravar reunião", keys: [])
        }
    }

    /// The offer slides in from the edge, compact, then opens into the full card.
    private func offerChanged(_ offer: MeetingOffer?) {
        guard let offer else {
            layout()
            return
        }
        let reduceMotion = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        state.offerExpanded = reduceMotion
        layout()
        guard !reduceMotion else { return }
        let final = panel.frame
        var start = final
        switch state.edge {
        case .bottom: start.origin.y -= 10
        case .left: start.origin.x -= 10
        case .right: start.origin.x += 10
        }
        panel.alphaValue = 0
        panel.setFrame(start, display: false)
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.25
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().setFrame(final, display: true)
            panel.animator().alphaValue = 1
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { [weak self] in
            guard let self, self.state.offer == offer, !self.state.offerExpanded else { return }
            withAnimation(.spring(response: 0.35, dampingFraction: 0.85)) { self.state.offerExpanded = true }
            self.layout(animated: true)
        }
    }

    /// The live transcript sits above the bar at the bottom, or beside it on a side.
    func layoutLive() {
        guard state.liveVisible, state.meetingSince != nil, panel.isVisible else {
            livePanel.hide()
            return
        }
        let size = LivePanel.size
        let bar = panel.frame
        let origin: NSPoint
        switch state.edge {
        case .bottom: origin = NSPoint(x: (bar.midX - size.width / 2).rounded(), y: bar.maxY + 10)
        case .left: origin = NSPoint(x: bar.maxX + 10, y: (bar.midY - size.height / 2).rounded())
        case .right: origin = NSPoint(x: bar.minX - size.width - 10, y: (bar.midY - size.height / 2).rounded())
        }
        livePanel.show(at: NSRect(origin: origin, size: size))
    }

    /// The bar's size for its state, or nil when it should not be on screen.
    private var currentSize: NSSize? {
        let side = state.edge != .bottom
        switch state.phase {
        case .hidden:
            let snoozed = hiddenUntil.map { $0 > Date() } ?? false
            guard (showsIdleBar && !snoozed) || state.meetingSince != nil || state.offer != nil || state.mistake else { return nil }
            if state.mistake { return Size.mistake }
            // The offer keeps its text on a side too: a card lying against that edge, not a column of icons.
            if state.offer != nil { return state.offerExpanded ? Size.offer : Size.offerCompact }
            // While a meeting records, hovering opens its controls (stop stays reachable), not the dictation.
            if state.hovering, state.meetingSince != nil { return side ? Size.meetingHoverSide : Size.meetingHover }
            if state.hovering { return side ? Size.hoveredSide : Size.hovered }
            if state.meetingSince != nil { return side ? Size.meetingSide : Size.meeting }
            return side ? Size.idleSide : Size.idle
        case .listening, .handsFree, .command:
            return side ? Size.recordingSide : Size.recording
        // Like the offer, it keeps its text on a side: a card lying against that edge.
        case .unplaced:
            return Size.unplaced
        default:
            return side ? Size.busySide : Size.busy
        }
    }

    /// Bottom center, or the middle of a side, 12 pt from the edge.
    static func slot(_ edge: DockEdge, size: NSSize, in frame: NSRect) -> NSPoint {
        switch edge {
        case .bottom: NSPoint(x: (frame.midX - size.width / 2).rounded(), y: frame.minY + 12)
        case .left: NSPoint(x: frame.minX + 12, y: (frame.midY - size.height / 2).rounded())
        case .right: NSPoint(x: frame.maxX - size.width - 12, y: (frame.midY - size.height / 2).rounded())
        }
    }

    // MARK: - Dragging

    private func dragChanged() {
        let mouse = NSEvent.mouseLocation
        if dragOffset == nil {
            dragOffset = NSPoint(x: mouse.x - panel.frame.minX, y: mouse.y - panel.frame.minY)
            state.dragging = true
            hintPanel.hide()
        }
        guard let offset = dragOffset, let screen = Self.mouseScreen else { return }
        panel.setFrameOrigin(NSPoint(x: mouse.x - offset.x, y: mouse.y - offset.y))
        guide.show(on: screen, nearest: nearestEdge(to: mouse, on: screen))
        panel.orderFrontRegardless()
    }

    private func dragEnded() {
        guard dragOffset != nil else { return }
        let mouse = NSEvent.mouseLocation
        let edge = Self.mouseScreen.map { nearestEdge(to: mouse, on: $0) } ?? state.edge
        dragOffset = nil
        state.dragging = false
        guide.hide()
        dock(at: edge)
    }

    /// The dock place whose slot is closest to the mouse.
    private func nearestEdge(to point: NSPoint, on screen: NSScreen) -> DockEdge {
        return DockEdge.allCases.min { a, b in distance(a) < distance(b) } ?? .bottom

        func distance(_ edge: DockEdge) -> CGFloat {
            let size = edge == .bottom ? Size.idle : Size.idleSide
            let origin = Self.slot(edge, size: size, in: screen.visibleFrame)
            let dx = origin.x + size.width / 2 - point.x, dy = origin.y + size.height / 2 - point.y
            return dx * dx + dy * dy
        }
    }

    private func dock(at edge: DockEdge) {
        state.edge = edge
        Settings.dictationBarEdge = edge
        layout(animated: true)
    }

    private func hide(for seconds: TimeInterval) {
        hiddenUntil = Date().addingTimeInterval(seconds)
        state.hovering = false
        layout()
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { [weak self] in
            self?.hiddenUntil = nil
            self?.layout()
        }
    }
}

/// Never becomes key, so the focused app keeps the keyboard and receives the paste.
private final class BarPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// Lets the first click on the bar act right away, although its window is never key.
private final class FirstClickHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

private struct BarView: View {
    @ObservedObject var state: OverlayState

    static let shape = RoundedRectangle(cornerRadius: 10, style: .continuous)

    var body: some View {
        content
            .id(contentKey)
            .transition(.opacity)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .animation(.easeOut(duration: 0.18), value: contentKey)
            // 10 pt continuous corners.
            .background(Self.shape.fill(Color.black.opacity(isIdleHandle ? 0.5 : 0.82)))
            .overlay(Self.shape.strokeBorder(Color.white.opacity(isIdleHandle ? 0.25 : 0.14)))
            .clipShape(Self.shape)
            .contentShape(Rectangle())
            .onHover { hovering in
                if state.phase == .hidden, !state.dragging { state.hovering = hovering }
                if !hovering { state.sideHint = nil }
            }
            // Positions come from the mouse in screen coordinates: the window moves under the gesture.
            .gesture(
                DragGesture(minimumDistance: 4)
                    .onChanged { _ in state.onDragChanged() }
                    .onEnded { _ in state.onDragEnded() }
            )
            .contextMenu {
                Button(state.meetingSince == nil ? "Gravar reunião" : "Parar gravação da reunião", action: state.onToggleMeeting)
                Divider()
                Button("Colar a última transcrição", action: state.onPasteLast)
                Menu("Microfone") { MicrophoneMenu(state: state) }
                Menu("Posição") {
                    ForEach(DockEdge.allCases) { edge in
                        Button { state.onSelectEdge(edge) } label: {
                            if state.edge == edge { Label(edge.title, systemImage: "checkmark") } else { Text(edge.title) }
                        }
                    }
                }
                Divider()
                Button("Esconder por 1 hora", action: state.onHideForAnHour)
                Button("Abrir o Lidiflow", action: state.onOpenApp)
            }
    }

    /// Which of the bar's layouts shows; a new one fades in while the bar grows or shrinks to it.
    private var contentKey: String {
        switch state.phase {
        case .error: "error"
        case .transforming: "transforming"
        default: "\(state.phase)"
        }
    }

    private var isIdleHandle: Bool {
        state.phase == .hidden && !state.hovering && state.meetingSince == nil && state.offer == nil && !state.mistake
    }

    @ViewBuilder private var content: some View {
        let side = state.edge != .bottom
        switch state.phase {
        case .hidden:
            if state.mistake {
                AnyView(MistakeView(resolve: state.onResolveMistake))
            } else if let offer = state.offer {
                AnyView(OfferView(offer: offer, expanded: state.offerExpanded, since: state.meetingSince,
                                  accept: { state.onAcceptOffer(offer) }, dismiss: state.onDismissOffer))
            } else if state.hovering, let since = state.meetingSince {
                AnyView(MeetingControls(since: since, vertical: side, liveVisible: state.liveVisible,
                                        dictate: state.onDictate, toggleLive: state.onToggleLive, stop: state.onToggleMeeting))
            } else if state.hovering {
                side ? AnyView(idleSide) : AnyView(idle)
            } else if let since = state.meetingSince {
                AnyView(MeetingPill(since: since, vertical: side, stop: state.onToggleMeeting))
            } else {
                Color.clear
            }
        case .listening, .handsFree, .command:
            side ? AnyView(recordingSide) : AnyView(recording)
        case .unplaced(let text):
            AnyView(UnplacedView(text: text, pasteKeys: state.pasteKeys, copy: state.onCopyUnplaced, dismiss: state.onDismissUnplaced))
        default:
            side ? AnyView(busySide) : AnyView(busy)
        }
    }

    // On a side: the same controls, stacked, without the long labels (they show on hover instead).

    private var idleSide: some View {
        VStack(spacing: 6) {
            BarButton(systemImage: "mic.fill", fill: Theme.blue, help: "Ditar sem segurar a tecla", action: state.onDictate,
                      onHover: { state.sideHint = $0 ? .dictate : (state.sideHint == .dictate ? nil : state.sideHint) })
            BarButton(systemImage: "record.circle", fill: Color.white.opacity(0.12), help: "Gravar reunião", action: state.onStartMeeting,
                      onHover: { state.sideHint = $0 ? .meeting : (state.sideHint == .meeting ? nil : state.sideHint) })
        }
        .padding(.vertical, 6)
    }

    private var recordingSide: some View {
        VStack(spacing: 8) {
            BarButton(systemImage: "xmark", fill: Color.white.opacity(0.12), help: "Cancelar", action: state.onCancel)
            Waveform(levels: Array(state.levels.suffix(5)), color: state.phase == .command ? .orange : Theme.blueGlow)
                .help(label)
            BarButton(systemImage: "stop.fill", fill: Theme.blue, help: "Finalizar e colar", action: state.onStop)
        }
        .padding(.vertical, 7)
    }

    private var busySide: some View {
        statusIcon.help(label)
    }

    /// Hovered idle bar: what dictates, and a button that starts hands-free.
    private var idle: some View {
        HStack(spacing: 8) {
            Text("Ditar")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Color.white)
            if !state.dictateKeys.isEmpty { KeyCaps(keys: state.dictateKeys, onBlue: true) }
            Spacer(minLength: 0)
            BarButton(systemImage: "mic.fill", fill: Theme.blue, help: "Ditar sem segurar a tecla", action: state.onDictate)
            BarButton(systemImage: "record.circle", fill: Color.white.opacity(0.12), help: "Gravar reunião", action: state.onStartMeeting)
        }
        .padding(.leading, 12)
        .padding(.trailing, 5)
    }

    private var recording: some View {
        HStack(spacing: 10) {
            BarButton(systemImage: "xmark", fill: Color.white.opacity(0.12), help: "Cancelar", action: state.onCancel)
            Waveform(levels: state.levels, color: state.phase == .command ? .orange : Theme.blueGlow)
            // The live transcript replaces the label once there is one; it shows its end and never grows the bar.
            Text(state.live.isEmpty ? label : state.live)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Color.white)
                .lineLimit(1)
                .truncationMode(.head)
                .frame(maxWidth: .infinity, alignment: .leading)
                .animation(nil, value: state.live)
            BarButton(systemImage: "stop.fill", fill: Theme.blue, help: "Finalizar e colar", action: state.onStop)
        }
        .padding(.horizontal, 6)
    }

    private var busy: some View {
        HStack(spacing: 10) {
            statusIcon
            Text(label)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Color.white)
                .lineLimit(1)
        }
        .padding(.horizontal, 16)
    }

    @ViewBuilder private var statusIcon: some View {
        switch state.phase {
        case .error:
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.yellow)
        default:
            ProgressView().controlSize(.small).tint(Color.white)
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
        case .unplaced: return ""
        case .hidden: return ""
        }
    }
}

private struct BarButton: View {
    let systemImage: String
    let fill: Color
    let help: String
    let action: () -> Void
    /// Set when the bar names the button in its own hint, in place of the system tooltip.
    var onHover: ((Bool) -> Void)? = nil

    var body: some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(Color.white)
                .frame(width: 26, height: 26)
                // Inset 4–7 pt from the bar's edge, so a smaller radius keeps the corners concentric.
                .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(fill))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(onHover == nil ? help : "")
        .accessibilityLabel(help)
        .onHover { onHover?($0) }
    }
}

/// The microphone choice from Settings, also reachable from the bar's menu.
private struct MicrophoneMenu: View {
    @ObservedObject var state: OverlayState

    var body: some View {
        item("Padrão do sistema", nil)
        Divider()
        ForEach(state.microphones?.all ?? []) { item($0.name, $0.id) }
    }

    private func item(_ title: String, _ value: String?) -> some View {
        Button {
            state.onSelectMicrophone(value)
        } label: {
            if state.microphone == value { Label(title, systemImage: "checkmark") } else { Text(title) }
        }
    }
}

/// While the bar is dragged: the three places it can dock, with the one it will snap to highlighted.
private final class DockGuideWindow {
    private let panel: NSPanel
    private let model = DockGuideModel()

    init() {
        panel = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        panel.level = .statusBar
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.contentView = NSHostingView(rootView: DockGuideView(model: model))
    }

    func show(on screen: NSScreen, nearest: DockEdge) {
        let frame = screen.visibleFrame
        if panel.frame != frame { panel.setFrame(frame, display: true) }
        // Each place is drawn the size of the opened bar there, in the window's top-left coordinates.
        model.slots = DockEdge.allCases.map { edge in
            let size = edge == .bottom ? NSSize(width: 168, height: 36) : NSSize(width: 52, height: 112)
            let origin = OverlayWindow.slot(edge, size: size, in: frame)
            return DockGuideModel.Slot(edge: edge, rect: CGRect(x: origin.x - frame.minX, y: frame.maxY - origin.y - size.height,
                                                                  width: size.width, height: size.height))
        }
        model.nearest = nearest
        panel.orderFrontRegardless()
    }

    func hide() {
        panel.orderOut(nil)
    }
}

private final class DockGuideModel: ObservableObject {
    struct Slot: Identifiable {
        let edge: DockEdge
        let rect: CGRect
        var id: DockEdge { edge }
    }

    @Published var slots: [Slot] = []
    @Published var nearest: DockEdge = .bottom
}

private struct DockGuideView: View {
    @ObservedObject var model: DockGuideModel

    var body: some View {
        ZStack(alignment: .topLeading) {
            ForEach(model.slots) { slot in
                let active = slot.edge == model.nearest
                BarView.shape
                    .fill(active ? Theme.blueGlow.opacity(0.28) : Color.black.opacity(0.18))
                    .overlay(BarView.shape
                        .strokeBorder(active ? Theme.blueGlow : Color.white.opacity(0.6),
                                      style: StrokeStyle(lineWidth: active ? 2 : 1, dash: active ? [] : [4, 3])))
                    .frame(width: slot.rect.width, height: slot.rect.height)
                    .offset(x: slot.rect.minX, y: slot.rect.minY)
                    .animation(.easeOut(duration: 0.12), value: model.nearest)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}

/// What the bar offers about a call the Lidiflow noticed.
enum MeetingOffer: Equatable {
    case record(app: String)
    case stop(app: String)

    var text: String {
        switch self {
        case .record(let app): "Reunião no \(app)"
        case .stop(let app): "A chamada no \(app) acabou"
        }
    }

    var action: String {
        switch self {
        case .record: "Gravar"
        case .stop: "Parar e resumir"
        }
    }

    var app: String {
        switch self {
        case .record(let app), .stop(let app): app
        }
    }

    var isStop: Bool { if case .stop = self { true } else { false } }
}

/// "Reunião detectada": compact while it slides in, then the card with the call and the record button.
private struct OfferView: View {
    let offer: MeetingOffer
    let expanded: Bool
    let since: Date?
    let accept: () -> Void
    let dismiss: () -> Void

    private static let green = Color(hex: 0x7FA88A)

    var body: some View {
        Group {
            if expanded { card.transition(.opacity) } else { compact.transition(.opacity) }
        }
        .help(offer.text)
    }

    private var compact: some View {
        HStack(spacing: 8) {
            Image(systemName: offer.isStop ? "stop.fill" : "video.fill")
                .font(.system(size: 12))
                .foregroundStyle(Theme.blueGlow)
            Text(offer.isStop ? "A chamada acabou" : "Reunião detectada")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Color.white)
            Spacer(minLength: 0)
            Circle().fill(Self.green).frame(width: 6, height: 6)
            Text("agora").font(.system(size: 12)).foregroundStyle(Color.white.opacity(0.6))
        }
        .padding(.horizontal, 14)
    }

    private var card: some View {
        HStack(spacing: 12) {
            Image(systemName: offer.isStop ? "stop.fill" : "video.fill")
                .font(.system(size: 15))
                .foregroundStyle(Theme.blueGlow)
                .frame(width: 36, height: 36)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Theme.blueGlow.opacity(0.2)))
            VStack(alignment: .leading, spacing: 3) {
                Text(offer.isStop ? "A chamada acabou" : "Reunião detectada")
                    .font(.system(size: 14, weight: .medium))
                    .foregroundStyle(Color.white)
                HStack(spacing: 5) {
                    Circle().fill(Self.green).frame(width: 6, height: 6)
                    Text(subtitle)
                        .font(.system(size: 12))
                        .foregroundStyle(Color.white.opacity(0.6))
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 0)
            Button(action: accept) {
                HStack(spacing: 6) {
                    Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 16, height: 16)
                    Text(offer.action).font(.system(size: 13, weight: .semibold))
                }
                .foregroundStyle(Color.black)
                .padding(.horizontal, 12)
                .frame(height: 34)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.white))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            BarButton(systemImage: "xmark", fill: Color.white.opacity(0.12), help: "Agora não", action: dismiss)
        }
        .padding(.horizontal, 12)
    }

    private var subtitle: String {
        guard offer.isStop, let since else { return "\(offer.app) · agora" }
        return "\(offer.app) · \(Meeting.clock(Date().timeIntervalSince(since))) gravados"
    }
}

/// A dictation that found no text box: what was said, to copy, or paste later with the shortcut.
private struct UnplacedView: View {
    let text: String
    let pasteKeys: [String]
    let copy: () -> Void
    let dismiss: () -> Void
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: "text.cursor").font(.system(size: 12, weight: .semibold)).foregroundStyle(Theme.blueGlow)
                Text("Nenhum campo de texto para colar").font(.system(size: 13, weight: .medium)).foregroundStyle(Color.white)
                Spacer(minLength: 0)
                Button(action: dismiss) {
                    Image(systemName: "xmark").font(.system(size: 10, weight: .semibold)).foregroundStyle(Color.white.opacity(0.6))
                        .frame(width: 20, height: 20).contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help("Fechar")
            }
            Text(text)
                .font(.system(size: 12))
                .foregroundStyle(Color.white.opacity(0.85))
                .lineLimit(2)
                .truncationMode(.tail)
                .frame(maxWidth: .infinity, alignment: .leading)
                .help(text)
            HStack(spacing: 6) {
                if !pasteKeys.isEmpty {
                    Text("Ou cole depois com").font(.system(size: 11)).foregroundStyle(Color.white.opacity(0.5))
                    KeyCaps(keys: pasteKeys, onBlue: true)
                }
                Spacer(minLength: 0)
                Button {
                    copy()
                    withAnimation(.easeOut(duration: 0.15)) { copied = true }
                } label: {
                    HStack(spacing: 5) {
                        Image(systemName: copied ? "checkmark" : "doc.on.doc").font(.system(size: 11, weight: .semibold))
                        Text(copied ? "Copiado" : "Copiar").font(.system(size: 13, weight: .semibold))
                    }
                    .foregroundStyle(Color.black)
                    .padding(.horizontal, 12)
                    .frame(height: 28)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.white))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
    }
}

/// "Começou sem querer?": a recording stopped with almost nothing said.
private struct MistakeView: View {
    let resolve: (Bool) -> Void

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 15))
                .foregroundStyle(Theme.blueGlow)
            VStack(alignment: .leading, spacing: 2) {
                Text("Começou sem querer?").font(.system(size: 14, weight: .medium)).foregroundStyle(Color.white)
                Text("Só algumas palavras foram captadas. Manter ou descartar a reunião?")
                    .font(.system(size: 12))
                    .foregroundStyle(Color.white.opacity(0.65))
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
            Button("Descartar") { resolve(false) }
                .buttonStyle(.plain)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Color.white)
            Button { resolve(true) } label: {
                Text("Manter")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Color.black)
                    .padding(.horizontal, 12)
                    .frame(height: 30)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.white))
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 14)
    }
}

/// Hovering the bar during a meeting: the time, dictate, the live transcript and stop.
private struct MeetingControls: View {
    let since: Date
    let vertical: Bool
    let liveVisible: Bool
    let dictate: () -> Void
    let toggleLive: () -> Void
    let stop: () -> Void

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            let elapsed = Int(context.date.timeIntervalSince(since))
            let minutes = String(format: "%02d", elapsed / 60), seconds = String(format: "%02d", elapsed % 60)
            if vertical {
                VStack(spacing: 6) {
                    Circle().fill(Theme.blueGlow).frame(width: 8, height: 8)
                    VStack(spacing: 0) {
                        Text(minutes)
                        Text(seconds)
                    }
                    .font(.system(size: 11, weight: .medium, design: .rounded))
                    .foregroundStyle(Color.white)
                    buttons
                }
                .padding(.vertical, 8)
            } else {
                HStack(spacing: 8) {
                    Circle().fill(Theme.blueGlow).frame(width: 8, height: 8)
                    Text("\(minutes):\(seconds)").font(.system(size: 12, weight: .medium, design: .rounded)).foregroundStyle(Color.white)
                    Spacer(minLength: 0)
                    buttons
                }
                .padding(.leading, 12)
                .padding(.trailing, 5)
            }
        }
    }

    @ViewBuilder private var buttons: some View {
        BarButton(systemImage: "mic.fill", fill: Color.white.opacity(0.12), help: "Ditar", action: dictate)
        BarButton(systemImage: "text.bubble", fill: liveVisible ? Color.white.opacity(0.3) : Color.white.opacity(0.12),
                  help: liveVisible ? "Esconder a transcrição" : "Ver a transcrição ao vivo", action: toggleLive)
        BarButton(systemImage: "stop.fill", fill: Theme.blue, help: "Parar e resumir", action: stop)
    }
}

/// The live transcript: a floating panel next to the bar while a meeting records.
/// "Ditar Shift dir. →" or "Gravar reunião →" beside the side bar's hovered button on a side, pointing at it. It never takes the mouse, so the bar keeps its hover.
private final class HintPanel {
    private let panel: NSPanel

    init() {
        panel = BarPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        panel.level = .statusBar
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.ignoresMouseEvents = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
    }

    func show(beside bar: NSRect, edge: DockEdge, centerFromTop: CGFloat, title: String, keys: [String]) {
        let view = NSHostingView(rootView: HintView(edge: edge, title: title, keys: keys))
        let size = view.fittingSize
        let y = (bar.maxY - centerFromTop - size.height / 2).rounded()
        let x = edge == .right ? bar.minX - 8 - size.width : bar.maxX + 8
        panel.contentView = view
        panel.setFrame(NSRect(x: x, y: y, width: size.width, height: size.height), display: true)
        guard !panel.isVisible else { return }
        panel.alphaValue = 0
        panel.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.12
            panel.animator().alphaValue = 1
        }
    }

    func hide() {
        panel.orderOut(nil)
    }
}

private struct HintView: View {
    let edge: DockEdge
    let title: String
    let keys: [String]

    var body: some View {
        HStack(spacing: 8) {
            if edge == .left { arrow }
            Text(title)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Color.white)
            if !keys.isEmpty { KeyCaps(keys: keys, onBlue: true) }
            if edge == .right { arrow }
        }
        .padding(.horizontal, 12)
        .frame(height: 34)
        .background(BarView.shape.fill(Color.black.opacity(0.94)))
        .overlay(BarView.shape.strokeBorder(Color.white.opacity(0.14)))
        .fixedSize()
    }

    private var arrow: some View {
        Image(systemName: edge == .right ? "arrow.right" : "arrow.left")
            .font(.system(size: 11, weight: .semibold))
            .foregroundStyle(Color.white.opacity(0.6))
    }
}

private final class LivePanel {
    static let size = NSSize(width: 340, height: 260)
    weak var state: OverlayState? {
        didSet { if let state { panel.contentView = FirstClickHostingView(rootView: LiveView(state: state)) } }
    }
    private let panel: NSPanel

    init() {
        panel = BarPanel(contentRect: NSRect(origin: .zero, size: Self.size), styleMask: [.borderless, .nonactivatingPanel],
                         backing: .buffered, defer: true)
        panel.level = .statusBar
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
    }

    func show(at frame: NSRect) {
        panel.setFrame(frame, display: true)
        panel.orderFrontRegardless()
    }

    func hide() {
        panel.orderOut(nil)
    }
}

private struct LiveView: View {
    @ObservedObject var state: OverlayState

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Circle().fill(Theme.blueGlow).frame(width: 7, height: 7)
                Text("AO VIVO").font(.system(size: 10, weight: .medium, design: .rounded)).tracking(0.8).foregroundStyle(Color.white)
                if let since = state.meetingSince {
                    TimelineView(.periodic(from: .now, by: 1)) { context in
                        Text(Meeting.clock(context.date.timeIntervalSince(since)))
                            .font(.system(size: 10, weight: .medium, design: .rounded))
                            .foregroundStyle(Color.white.opacity(0.6))
                    }
                }
                Spacer()
                Button(action: state.onToggleLive) {
                    Image(systemName: "xmark").font(.system(size: 10, weight: .semibold)).foregroundStyle(Color.white.opacity(0.7))
                }
                .buttonStyle(.plain)
                .help("Esconder a transcrição")
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            Rectangle().fill(Color.white.opacity(0.1)).frame(height: 1)
            if state.meetingLive.isEmpty {
                Text("Ouvindo… as falas aparecem aqui assim que cada um terminar de falar.")
                    .font(.system(size: 12))
                    .foregroundStyle(Color.white.opacity(0.6))
                    .padding(14)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        VStack(alignment: .leading, spacing: 10) {
                            ForEach(Array(state.meetingLive.enumerated()), id: \.offset) { index, segment in
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(segment.source == .me ? "Você" : "Eles")
                                        .font(.system(size: 11, weight: .semibold))
                                        .foregroundStyle(segment.source == .me ? Theme.blueGlow : Color.white)
                                    Text(segment.text)
                                        .font(.system(size: 12))
                                        .foregroundStyle(Color.white.opacity(0.9))
                                        .fixedSize(horizontal: false, vertical: true)
                                }
                                .id(index)
                                .transition(.opacity.combined(with: .offset(y: 6)))
                            }
                        }
                        .padding(14)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .onChange(of: state.meetingLive.count) { _, count in
                        withAnimation(.easeOut(duration: 0.25)) { proxy.scrollTo(count - 1, anchor: .bottom) }
                    }
                    .onAppear { proxy.scrollTo(state.meetingLive.count - 1, anchor: .bottom) }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(BarView.shape.fill(Color.black.opacity(0.94)))
        .overlay(BarView.shape.strokeBorder(Color.white.opacity(0.14)))
        .clipShape(BarView.shape)
    }
}

/// The idle bar while a meeting is recorded: a dot, the elapsed time and a stop button.
private struct MeetingPill: View {
    let since: Date
    let vertical: Bool
    let stop: () -> Void

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            let elapsed = Int(context.date.timeIntervalSince(since))
            let minutes = String(format: "%02d", elapsed / 60), seconds = String(format: "%02d", elapsed % 60)
            if vertical {
                VStack(spacing: 4) {
                    dot
                    Text(minutes).font(.system(size: 11, weight: .medium, design: .rounded)).foregroundStyle(Color.white)
                    Text(seconds).font(.system(size: 11, weight: .medium, design: .rounded)).foregroundStyle(Color.white)
                    stopButton
                }
                .padding(.vertical, 6)
            } else {
                HStack(spacing: 8) {
                    dot
                    Text("\(minutes):\(seconds)").font(.system(size: 12, weight: .medium, design: .rounded)).foregroundStyle(Color.white)
                    Spacer(minLength: 0)
                    stopButton
                }
                .padding(.leading, 12)
                .padding(.trailing, 4)
            }
        }
        .help("Gravando reunião")
    }

    private var dot: some View {
        Circle().fill(Theme.blueGlow).frame(width: 8, height: 8)
    }

    private var stopButton: some View {
        Button(action: stop) {
            Image(systemName: "stop.fill")
                .font(.system(size: 9, weight: .semibold))
                .foregroundStyle(Color.white)
                .frame(width: 20, height: 20)
                .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(Theme.blueGlow))
        }
        .buttonStyle(.plain)
        .help("Parar gravação da reunião")
    }
}
