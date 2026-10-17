import CoreAudio
import Foundation

/// An audio input device. `id` is its Core Audio UID, which survives reconnects and restarts
/// (the numeric AudioDeviceID doesn't).
struct Microphone: Identifiable, Hashable {
    let id: String
    let name: String
}

/// The Mac's input devices, kept current as they're plugged in and out.
final class MicrophoneList: ObservableObject {
    @Published private(set) var all: [Microphone] = []
    /// Name of the system default input, shown next to "Padrão do sistema".
    @Published private(set) var systemDefault: String?

    private static var system = AudioObjectPropertyAddress(selector: kAudioHardwarePropertyDevices)
    private static var defaultInput = AudioObjectPropertyAddress(selector: kAudioHardwarePropertyDefaultInputDevice)

    init() {
        refresh()
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.refresh() }
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &Self.system, .main, listener)
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &Self.defaultInput, .main, listener)
    }

    func refresh() {
        all = Self.inputDevices().compactMap { device in
            guard let uid = Self.string(device, kAudioDevicePropertyDeviceUID),
                  let name = Self.string(device, kAudioObjectPropertyName) else { return nil }
            return Microphone(id: uid, name: name)
        }
        systemDefault = Self.defaultInputDevice().flatMap { Self.string($0, kAudioObjectPropertyName) }
    }

    /// The device for a saved UID, or nil when it's not connected.
    static func device(uid: String) -> AudioDeviceID? {
        inputDevices().first { string($0, kAudioDevicePropertyDeviceUID) == uid }
    }

    // MARK: - Core Audio

    private static func inputDevices() -> [AudioDeviceID] {
        var address = system
        var size: UInt32 = 0
        let object = AudioObjectID(kAudioObjectSystemObject)
        guard AudioObjectGetPropertyDataSize(object, &address, 0, nil, &size) == noErr else { return [] }
        var devices = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(object, &address, 0, nil, &size, &devices) == noErr else { return [] }
        return devices.filter(hasInput)
    }

    private static func defaultInputDevice() -> AudioDeviceID? {
        var address = defaultInput
        var device = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device)
        return status == noErr && device != 0 ? device : nil
    }

    private static func hasInput(_ device: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(selector: kAudioDevicePropertyStreams, scope: kAudioObjectPropertyScopeInput)
        var size: UInt32 = 0
        return AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size) == noErr && size > 0
    }

    private static func string(_ device: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(selector: selector)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr else { return nil }
        return value?.takeRetainedValue() as String?
    }
}

private extension AudioObjectPropertyAddress {
    init(selector: AudioObjectPropertySelector, scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) {
        self.init(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }
}
