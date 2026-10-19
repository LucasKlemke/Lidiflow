using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Lidiflow.Platform;

/// The recording devices, and which one to record from.
///
/// Recording from a Bluetooth headset's microphone switches it from its music profile to its hands-free profile:
/// the sound drops to phone quality and most players pause. So, by default, a Bluetooth microphone is swapped for
/// another one (the PC's or a USB microphone), and the headphones keep playing.
public static class Microphones
{
    public sealed record Device(string Id, string Name, bool IsBluetooth);

    /// The value stored in AppSettings.Microphone for the automatic choice.
    public const string Automatic = "auto";

    /// PKEY_Device_EnumeratorName: the bus that enumerated the device; Bluetooth ones start with "BTH"
    /// (BTHENUM, BTHHFENUM for hands-free, BTHLEDEVICE).
    private static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    public static List<Device> Inputs()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(Describe).ToList();
        }
        catch
        {
            return new List<Device>();
        }
    }

    /// The WaveIn device number to record from for a setting: a chosen microphone if it is still connected,
    /// otherwise the Windows default, unless that is Bluetooth and another microphone exists. -1 is the default.
    public static int Resolve(string setting)
    {
        try
        {
            var inputs = Inputs();
            var chosen = setting == Automatic ? null : inputs.FirstOrDefault(d => d.Id == setting);
            if (chosen == null)
            {
                using var enumerator = new MMDeviceEnumerator();
                var system = Describe(enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console));
                if (!system.IsBluetooth) return -1;
                chosen = inputs.FirstOrDefault(d => !d.IsBluetooth);
                if (chosen == null) return -1;
            }
            return WaveInNumber(chosen.Name);
        }
        catch
        {
            return -1;
        }
    }

    private static Device Describe(MMDevice device)
    {
        string? bus = null;
        try
        {
            if (device.Properties.Contains(EnumeratorName)) bus = device.Properties[EnumeratorName].Value as string;
        }
        catch
        {
            // Some drivers expose no property store; treat them as wired.
        }
        var name = device.FriendlyName;
        bool bluetooth = bus?.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) == true
                         || name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);
        return new Device(device.ID, name, bluetooth);
    }

    /// WaveIn only knows devices by number and by a name cut to 31 characters, so match on that prefix.
    private static int WaveInNumber(string name)
    {
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var product = WaveInEvent.GetCapabilities(i).ProductName;
            if (product.Length > 0 && name.StartsWith(product, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}
