import Foundation

/// Listens to the chosen microphone while Ajustes is open, so its waveform shows whether it hears you.
/// Keeps no audio, only the levels.
final class MicrophoneMonitor: ObservableObject {
    @Published private(set) var levels = [Float](repeating: 0, count: 16)
    @Published private(set) var failed = false

    private let recorder = AudioRecorder(keepsSamples: false)
    private(set) var isRunning = false

    init() {
        recorder.onLevel = { [weak self] level in
            DispatchQueue.main.async { self?.levels.pushLevel(level) }
        }
    }

    /// Starts, or restarts on the microphone now in `Settings.microphone`.
    func start() {
        stop()
        do {
            try recorder.start()
            isRunning = true
            failed = false
        } catch {
            failed = true
        }
    }

    func stop() {
        guard isRunning else { return }
        _ = recorder.stop()
        isRunning = false
        levels = levels.map { _ in 0 }
    }
}
