import AudioToolbox
import AVFoundation

/// Captures the chosen microphone (or the system default) and resamples it to 16 kHz mono, which is what Whisper expects.
final class AudioRecorder {
    static let sampleRate: Double = 16_000

    var onLevel: (Float) -> Void = { _ in }

    private var engine = AVAudioEngine()
    /// The input `engine` was set to, nil for the system default.
    private var device: AudioDeviceID?
    private let targetFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: 1, interleaved: false
    )!
    private var converter: AVAudioConverter?
    private var samples: [Float] = []
    private let lock = NSLock()
    /// False for a recorder that only reports levels (the microphone test in Ajustes).
    private let keepsSamples: Bool

    init(keepsSamples: Bool = true) {
        self.keepsSamples = keepsSamples
    }

    func start() throws {
        lock.withLock { samples.removeAll(keepingCapacity: true) }
        useDevice(Settings.microphone.flatMap(MicrophoneList.device(uid:)))
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        converter = AVAudioConverter(from: format, to: targetFormat)
        input.installTap(onBus: 0, bufferSize: 4096, format: format) { [weak self] buffer, _ in
            self?.process(buffer)
        }
        engine.prepare()
        do {
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            engine.stop()
            throw error
        }
    }

    /// Points the engine at `device`. A fresh engine for each change, since one that already ran keeps
    /// the old input's format. A chosen microphone that's unplugged falls back to the system default.
    private func useDevice(_ device: AudioDeviceID?) {
        guard device != self.device else { return }
        engine = AVAudioEngine()
        self.device = device
        guard var id = device, let unit = engine.inputNode.audioUnit else { return }
        let status = AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                          &id, UInt32(MemoryLayout<AudioDeviceID>.size))
        if status != noErr {
            engine = AVAudioEngine()
            self.device = nil
        }
    }

    /// The last `seconds` of the recording, without stopping. Copies only that tail, so the
    /// audio thread waits on the lock for the same short time however long the recording gets.
    func tail(seconds: Double) -> [Float] {
        let count = Int(Self.sampleRate * seconds)
        return lock.withLock { Array(samples.suffix(count)) }
    }

    /// Stops capture and returns the recorded samples.
    func stop() -> [Float] {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        return lock.withLock { samples }
    }

    private func process(_ buffer: AVAudioPCMBuffer) {
        guard let converter else { return }
        let ratio = targetFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 32
        guard let output = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else { return }

        var consumed = false
        var error: NSError?
        converter.convert(to: output, error: &error) { _, status in
            if consumed {
                status.pointee = .noDataNow
                return nil
            }
            consumed = true
            status.pointee = .haveData
            return buffer
        }
        guard error == nil, let channel = output.floatChannelData?[0] else { return }

        let chunk = Array(UnsafeBufferPointer(start: channel, count: Int(output.frameLength)))
        if keepsSamples { lock.withLock { samples.append(contentsOf: chunk) } }

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
