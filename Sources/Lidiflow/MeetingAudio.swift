import AVFoundation
import AudioToolbox
import CoreAudio
import Foundation

/// Streams 16 kHz mono 16-bit PCM straight to a WAV file, so an hour-long meeting never sits in memory.
/// The header carries the sizes, which are written when the file is closed (and repaired by `repair` after a crash).
final class WavFileWriter {
    static let sampleRate: Double = 16_000
    private static let headerSize: UInt64 = 44

    private let handle: FileHandle
    private var dataBytes: UInt32 = 0
    private let lock = NSLock()

    init(url: URL) throws {
        FileManager.default.createFile(atPath: url.path, contents: nil)
        handle = try FileHandle(forWritingTo: url)
        try handle.write(contentsOf: Self.header(dataBytes: 0))
    }

    var duration: TimeInterval { lock.withLock { Double(dataBytes) / 2 / Self.sampleRate } }

    func append(_ samples: [Float]) {
        var data = Data(capacity: samples.count * 2)
        for sample in samples {
            var value = Int16(max(-1, min(1, sample)) * Float(Int16.max)).littleEndian
            withUnsafeBytes(of: &value) { data.append(contentsOf: $0) }
        }
        lock.withLock {
            try? handle.write(contentsOf: data)
            dataBytes += UInt32(data.count)
        }
    }

    func close() {
        lock.withLock {
            try? handle.seek(toOffset: 0)
            try? handle.write(contentsOf: Self.header(dataBytes: dataBytes))
            try? handle.close()
        }
    }

    /// Rewrites the header from the file's size, for a recording the app could not close (crash, power loss).
    static func repair(_ url: URL) {
        guard let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? UInt64),
              size >= headerSize, let handle = try? FileHandle(forWritingTo: url) else { return }
        try? handle.write(contentsOf: header(dataBytes: UInt32(min(size - headerSize, UInt64(UInt32.max)))))
        try? handle.close()
    }

    static func header(dataBytes: UInt32) -> Data {
        var data = Data()
        func append<T: FixedWidthInteger>(_ value: T) {
            withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) }
        }
        data.append(contentsOf: Array("RIFF".utf8)); append(36 + dataBytes)
        data.append(contentsOf: Array("WAVE".utf8))
        data.append(contentsOf: Array("fmt ".utf8)); append(UInt32(16))
        append(UInt16(1)); append(UInt16(1))
        append(UInt32(sampleRate)); append(UInt32(sampleRate * 2))
        append(UInt16(2)); append(UInt16(16))
        data.append(contentsOf: Array("data".utf8)); append(dataBytes)
        return data
    }

    /// Samples between two times, read from disk without loading the whole file.
    static func samples(in url: URL, from start: TimeInterval, to end: TimeInterval) -> [Float] {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return [] }
        defer { try? handle.close() }
        let first = UInt64(max(0, start) * sampleRate), count = Int(max(0, end - start) * sampleRate)
        try? handle.seek(toOffset: headerSize + first * 2)
        guard let data = try? handle.read(upToCount: count * 2) else { return [] }
        return data.withUnsafeBytes { raw in
            raw.bindMemory(to: Int16.self).map { Float(Int16(littleEndian: $0)) / Float(Int16.max) }
        }
    }

    /// RMS of each `window` of samples, streamed from disk.
    static func levels(in url: URL, window: Int) -> [Float] {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return [] }
        defer { try? handle.close() }
        try? handle.seek(toOffset: headerSize)
        var levels: [Float] = []
        while let data = try? handle.read(upToCount: window * 2 * 1000), !data.isEmpty {
            data.withUnsafeBytes { raw in
                let samples = raw.bindMemory(to: Int16.self)
                var index = 0
                while index < samples.count {
                    let end = min(index + window, samples.count)
                    var power: Float = 0
                    for i in index..<end {
                        let value = Float(Int16(littleEndian: samples[i])) / Float(Int16.max)
                        power += value * value
                    }
                    levels.append(sqrt(power / Float(end - index)))
                    index = end
                }
            }
        }
        return levels
    }
}

/// Records what the Mac plays (the other people in a call) through a Core Audio process tap, excluding the
/// Lidiflow itself, converted to 16 kHz mono. macOS 14.2+; the first start asks for "System Audio Recording".
@available(macOS 14.2, *)
final class SystemAudioRecorder {
    enum Failure: LocalizedError {
        case coreAudio(String, OSStatus)

        var errorDescription: String? {
            switch self {
            case .coreAudio(let step, let status): "Não foi possível gravar o áudio do sistema (\(step), \(status))"
            }
        }
    }

    var onSamples: ([Float]) -> Void = { _ in }

    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private var converter: AVAudioConverter?
    private let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: WavFileWriter.sampleRate,
                                       channels: 1, interleaved: false)!
    private let queue = DispatchQueue(label: "com.lidiflow.system-audio")

    func start() throws {
        let description = CATapDescription(stereoGlobalTapButExcludeProcesses: Self.ownProcessObjects())
        description.uuid = UUID()
        description.muteBehavior = .unmuted
        description.isPrivate = true

        var tap = AudioObjectID(kAudioObjectUnknown)
        try check(AudioHardwareCreateProcessTap(description, &tap), "tap")
        tapID = tap

        var stream = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        var address = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        try check(AudioObjectGetPropertyData(tap, &address, 0, nil, &size, &stream), "formato")
        guard let format = AVAudioFormat(streamDescription: &stream) else { throw Failure.coreAudio("formato", -1) }
        converter = AVAudioConverter(from: format, to: target)

        let output = try Self.defaultOutputUID()
        let aggregate: [String: Any] = [
            kAudioAggregateDeviceNameKey: "Lidiflow reunião",
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: output,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: output]],
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapDriftCompensationKey: true,
                                               kAudioSubTapUIDKey: description.uuid.uuidString]],
        ]
        var device = AudioObjectID(kAudioObjectUnknown)
        try check(AudioHardwareCreateAggregateDevice(aggregate as CFDictionary, &device), "dispositivo")
        aggregateID = device

        try check(AudioDeviceCreateIOProcIDWithBlock(&procID, device, queue) { [weak self] _, input, _, _, _ in
            self?.process(input, format: format)
        }, "leitura")
        try check(AudioDeviceStart(device, procID), "início")
    }

    func stop() {
        if aggregateID != kAudioObjectUnknown {
            AudioDeviceStop(aggregateID, procID)
            if let procID { AudioDeviceDestroyIOProcID(aggregateID, procID) }
            AudioHardwareDestroyAggregateDevice(aggregateID)
        }
        if tapID != kAudioObjectUnknown { AudioHardwareDestroyProcessTap(tapID) }
        procID = nil
        aggregateID = kAudioObjectUnknown
        tapID = kAudioObjectUnknown
        queue.sync {}
    }

    private func process(_ list: UnsafePointer<AudioBufferList>, format: AVAudioFormat) {
        guard let converter,
              let buffer = AVAudioPCMBuffer(pcmFormat: format, bufferListNoCopy: list, deallocator: nil),
              buffer.frameLength > 0 else { return }
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * target.sampleRate / format.sampleRate) + 32
        guard let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else { return }
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
        guard error == nil, let channel = output.floatChannelData?[0], output.frameLength > 0 else { return }
        onSamples(Array(UnsafeBufferPointer(start: channel, count: Int(output.frameLength))))
    }

    private func check(_ status: OSStatus, _ step: String) throws {
        guard status == noErr else { throw Failure.coreAudio(step, status) }
    }

    /// The Lidiflow's own audio process, so its sounds (the dictation pops) stay out of the recording.
    private static func ownProcessObjects() -> [AudioObjectID] {
        var pid = ProcessInfo.processInfo.processIdentifier
        var object = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyTranslatePIDToProcessObject,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address,
                                                UInt32(MemoryLayout<pid_t>.size), &pid, &size, &object)
        return status == noErr && object != kAudioObjectUnknown ? [object] : []
    }

    private static func defaultOutputUID() throws -> String {
        var device = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device)
        guard status == noErr else { throw Failure.coreAudio("saída", status) }
        var uid: Unmanaged<CFString>?
        size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        address.mSelector = kAudioDevicePropertyDeviceUID
        status = AudioObjectGetPropertyData(device, &address, 0, nil, &size, &uid)
        guard status == noErr, let uid else { throw Failure.coreAudio("saída", status) }
        return uid.takeRetainedValue() as String
    }
}
