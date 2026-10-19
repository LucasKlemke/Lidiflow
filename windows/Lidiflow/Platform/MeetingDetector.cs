using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Lidiflow.Core;
using Microsoft.Win32;

namespace Lidiflow.Platform;

/// Notices a call: a meeting app using the microphone (docs/reunioes.md, "Fase 2"). Every 3 s it reads which apps
/// hold the microphone from the Capability Access Manager in the registry; a browser counts only when one of its
/// windows is titled like a meeting. A call ends after 8 s without its app on the microphone.
public sealed class MeetingDetector
{
    private const string ConsentStore =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private static readonly Dictionary<string, string> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Zoom.exe"] = "Zoom",
        ["ms-teams.exe"] = "Microsoft Teams",
        ["Teams.exe"] = "Microsoft Teams",
        ["CiscoCollabHost.exe"] = "Webex",
        ["webexmta.exe"] = "Webex",
        ["slack.exe"] = "Slack",
        ["Discord.exe"] = "Discord",
    };

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "arc.exe",
    };

    /// Window-title words that make a browser's microphone use a meeting, and the service they name.
    private static readonly (string Word, string Name)[] TitleWords =
    {
        // "Meet" last: a Teams or Zoom title often says "meeting".
        ("meet.google.com", "Google Meet"), ("Microsoft Teams", "Microsoft Teams"), ("Teams", "Microsoft Teams"),
        ("Zoom", "Zoom"), ("Whereby", "Whereby"), ("Jitsi", "Jitsi"), ("Meet", "Google Meet"),
    };

    private static readonly TimeSpan EndAfter = TimeSpan.FromSeconds(8);

    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool checking;
    private DateTime? lastSeen;

    /// The call going on now, if any.
    public string? Current { get; private set; }
    public Func<bool> Enabled { get; set; } = () => true;
    public event Action<string>? CallStarted;
    public event Action<string>? CallEnded;

    public MeetingDetector()
    {
        timer.Tick += async (_, _) => await CheckAsync();
    }

    public void Start() => timer.Start();

    public void Stop() => timer.Stop();

    private async Task CheckAsync()
    {
        if (checking) return;
        if (!Enabled())
        {
            Current = null;
            lastSeen = null;
            return;
        }
        checking = true;
        string? found;
        try
        {
            found = await Task.Run(FindCall);
        }
        catch (Exception e)
        {
            Log.Write("meeting detection: " + e.Message);
            found = null;
        }
        finally
        {
            checking = false;
        }

        var now = DateTime.Now;
        if (found != null)
        {
            lastSeen = now;
            if (Current == null)
            {
                Current = found;
                CallStarted?.Invoke(found);
            }
        }
        else if (Current != null && lastSeen is { } seen && now - seen >= EndAfter)
        {
            var ended = Current;
            Current = null;
            lastSeen = null;
            CallEnded?.Invoke(ended);
        }
    }

    /// The meeting app on the microphone right now, by its display name.
    private static string? FindCall()
    {
        foreach (var exe in AppsOnMicrophone())
        {
            var name = Path.GetFileName(exe);
            if (Apps.TryGetValue(name, out var app)) return app;
            if (Browsers.Contains(name) && MeetingInBrowser(name) is { } service) return service;
        }
        return null;
    }

    /// Executable paths (or package names) that hold the microphone now, minus the Lidiflow itself.
    private static IEnumerable<string> AppsOnMicrophone()
    {
        using var store = Registry.CurrentUser.OpenSubKey(ConsentStore);
        if (store == null) yield break;
        var own = Environment.ProcessPath ?? "";

        using (var nonPackaged = store.OpenSubKey("NonPackaged"))
        {
            if (nonPackaged != null)
            {
                foreach (var keyName in nonPackaged.GetSubKeyNames())
                {
                    using var key = nonPackaged.OpenSubKey(keyName);
                    if (!InUse(key)) continue;
                    // The key is the executable's path with '#' for '\'.
                    var path = keyName.Replace('#', '\\');
                    if (string.Equals(path, own, StringComparison.OrdinalIgnoreCase)) continue;
                    yield return path;
                }
            }
        }

        foreach (var package in store.GetSubKeyNames())
        {
            if (package.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
            using var key = store.OpenSubKey(package);
            if (!InUse(key)) continue;
            // The new Teams is a packaged app; map it to the name the table knows.
            if (package.StartsWith("MSTeams_", StringComparison.OrdinalIgnoreCase)) yield return "ms-teams.exe";
        }
    }

    private static bool InUse(RegistryKey? key)
    {
        if (key == null) return false;
        long start = ReadLong(key, "LastUsedTimeStart"), stop = ReadLong(key, "LastUsedTimeStop");
        return start > 0 && stop == 0;
    }

    private static long ReadLong(RegistryKey key, string name) => key.GetValue(name) switch
    {
        long value => value,
        int value => value,
        _ => 0,
    };

    /// The meeting service named by a visible window of the browser, if any.
    private static string? MeetingInBrowser(string exe)
    {
        var pids = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))
            .Select(process =>
            {
                using (process) return (uint)process.Id;
            })
            .ToHashSet();
        if (pids.Count == 0) return null;

        string? service = null;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var pid);
            if (!pids.Contains(pid)) return true;
            int length = GetWindowTextLength(window);
            if (length == 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(window, title, title.Capacity);
            var text = title.ToString();
            foreach (var (word, name) in TitleWords)
            {
                if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    service = name;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return service;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
}
