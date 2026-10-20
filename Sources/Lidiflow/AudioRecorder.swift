import AVFoundation

/// Captures the chosen microphone (or the system default) as 16 kHz mono, which is what Whisper expects.
///
/// Uses a capture session, which opens one specific device and converts to the format we ask for. AVAudioEngine,
/// used before, keeps the previous device's format when the input changes under it (AirPods connecting while
/// "Padrão do sistema" is selected): the tap then fails, or raises an exception that kills the app (AirPods at
/// 24 kHz against a 48 kHz built-in microphone).
final class AudioRecorder: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate {
    enum Failure: LocalizedError {
        case noMicrophone

        var errorDescription: String? { "Microfone indisponível" }
    }

    static let sampleRate: Double = 16_000

    var onLevel: (Float) -> Void = { _ in }
    /// Every chunk as it arrives, for recordings written straight to disk (meetings).
    var onSamples: (([Float]) -> Void)?

    private var session: AVCaptureSession?
    /// Delivers the buffers in order; `stop()` drains it so the last words are not cut.
    private let queue = DispatchQueue(label: "com.lidiflow.recorder")
    private var samples: [Float] = []
    private let lock = NSLock()
    /// False for a recorder that only reports levels (the microphone test in Ajustes), or a long one that streams
    /// through `onSamples` instead of piling up in memory (meetings).
    var keepsSamples: Bool

    init(keepsSamples: Bool = true) {
        self.keepsSamples = keepsSamples
    }

    func start() throws {
        lock.withLock { samples.removeAll(keepingCapacity: true) }
        // Core Audio's device UID is the capture device's uniqueID.
        let chosen = MicrophoneList.recordingDevice(for: Settings.microphone).flatMap { AVCaptureDevice(uniqueID: $0) }
        guard let device = chosen ?? AVCaptureDevice.default(for: .audio) else { throw Failure.noMicrophone }

        let session = AVCaptureSession()
        let input = try AVCaptureDeviceInput(device: device)
        let output = AVCaptureAudioDataOutput()
        output.audioSettings = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: Self.sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false,
            AVLinearPCMIsBigEndianKey: false,
        ]
        output.setSampleBufferDelegate(self, queue: queue)
        guard session.canAddInput(input), session.canAddOutput(output) else { throw Failure.noMicrophone }
        session.addInput(input)
        session.addOutput(output)
        session.startRunning()
        self.session = session
    }

    /// The last `seconds` of the recording, without stopping. Copies only that tail, so the
    /// audio thread waits on the lock for the same short time however long the recording gets.
    func tail(seconds: Double) -> [Float] {
        let count = Int(Self.sampleRate * seconds)
        return lock.withLock { Array(samples.suffix(count)) }
    }

    /// Stops capture and returns the recorded samples.
    func stop() -> [Float] {
        session?.stopRunning()
        session = nil
        queue.sync {}
        return lock.withLock { samples }
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard let block = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }
        var length = 0
        var pointer: UnsafeMutablePointer<CChar>?
        guard CMBlockBufferGetDataPointer(block, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &length,
                                          dataPointerOut: &pointer) == kCMBlockBufferNoErr, let pointer else { return }
        let count = length / MemoryLayout<Float>.size
        guard count > 0 else { return }
        let chunk = pointer.withMemoryRebound(to: Float.self, capacity: count) { Array(UnsafeBufferPointer(start: $0, count: count)) }
        if keepsSamples { lock.withLock { samples.append(contentsOf: chunk) } }
        onSamples?(chunk)

        // A level per ~40 ms rather than per buffer (often ~100 ms), so the waveform moves smoothly.
        let step = Int(Self.sampleRate * 0.04)
        for start in stride(from: 0, to: chunk.count, by: step) {
            let part = chunk[start..<min(start + step, chunk.count)]
            onLevel(sqrt(part.reduce(0) { $0 + $1 * $1 } / Float(part.count)))
        }
    }

    /// Whether any 30 ms stretch is louder than room noise (about -42 dBFS). Speech peaks well above
    /// that even on quiet microphones; a muted or wrong input stays below it.
    static func hasVoice(_ samples: [Float]) -> Bool {
        let frame = Int(sampleRate * 0.03)
        return stride(from: 0, to: samples.count, by: frame).contains { start in
            let chunk = samples[start..<min(start + frame, samples.count)]
            return chunk.reduce(0) { $0 + $1 * $1 } / Float(chunk.count) > 0.008 * 0.008
        }
    }

    /// Encodes samples as a 16-bit PCM mono WAV file.
    static func wavData(from samples: [Float]) -> Data {
        var data = Data()
        func append<T: FixedWidthInteger>(_ value: T) {
            withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) }
        }
        let byteCount = UInt32(samples.count * 2)
        data.append(contentsOf: Array("RIFF".utf8)); append(36 + byteCount)
        data.append(contentsOf: Array("WAVE".utf8))
        data.append(contentsOf: Array("fmt ".utf8)); append(UInt32(16))
        append(UInt16(1)); append(UInt16(1))
        append(UInt32(sampleRate)); append(UInt32(sampleRate * 2))
        append(UInt16(2)); append(UInt16(16))
        data.append(contentsOf: Array("data".utf8)); append(byteCount)
        for sample in samples {
            append(Int16(max(-1, min(1, sample)) * Float(Int16.max)))
        }
        return data
    }
}
