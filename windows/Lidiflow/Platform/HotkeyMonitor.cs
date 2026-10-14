using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Lidiflow.Core;
using Microsoft.Win32;

namespace Lidiflow.Platform;

public enum HotkeyKind { HoldStart, HoldEnd, SwitchToCommand, HandsFree, Stop, Cancel, Discard, PasteLast, CopyLast, Transform }

public readonly record struct HotkeyEvent(HotkeyKind Kind, ShortcutAction Action = ShortcutAction.Dictate, int Index = 0);

/// Global shortcuts from low-level keyboard and mouse hooks (the CGEventTap counterpart).
/// Every action has up to two user-defined shortcuts: a key combo, modifiers alone, or an extra mouse button.
///
/// Modifier-only holds arm for a moment before starting, so typing Ctrl+C or a capital letter
/// with a modifier-only shortcut doesn't start a dictation.
///
/// The hooks run on their own thread, so slow work on the UI thread (clipboard, files) never delays
/// input system-wide or gets them dropped by Windows' hook timeout. Actions are posted to the UI thread.
public sealed class HotkeyMonitor : IDisposable
{
    private enum Hold { None, Armed, Active, Consumed }

    /// Key codes for 1…9, used by the positional transform shortcuts.
    public static readonly int[] Digits = { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 };

    private Dictionary<ShortcutAction, List<KeyCombo>> shortcuts = ShortcutActions.Defaults();

    /// Set from the UI thread; the hook thread reads its own copy.
    public Dictionary<ShortcutAction, List<KeyCombo>> Shortcuts
    {
        get => shortcuts;
        set => shortcuts = value.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
    }
    public Action<HotkeyEvent> OnAction { get; set; } = _ => { };
    public Func<bool> IsRecording { get; set; } = () => false;
    public Func<bool> IsHandsFree { get; set; } = () => false;
    /// Index of the transform bound to a key event, given whether a dictation hold is down.
    public Func<int, Mods, bool, int?> TransformIndex { get; set; } = (_, _, _) => null;

    private readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    private Dispatcher? hookThread;
    private DispatcherTimer arming = null!;
    // Kept in fields so the GC doesn't collect the delegates while Windows still calls them.
    private readonly Native.HookProc keyboardProc;
    private readonly Native.HookProc mouseProc;
    private IntPtr keyboardHook;
    private IntPtr mouseHook;

    private Hold hold = Hold.None;
    private ShortcutAction holdAction;
    private KeyCombo? holdCombo;
    private Mods consumedFlags;
    /// A non-hold action bound to modifiers alone fires when they're released untouched.
    private (ShortcutAction Action, KeyCombo Combo)? modifierTap;
    /// A hold started by a key or mouse button, released by its key-up / button-up.
    private (ShortcutAction Action, int? Key, int? Mouse)? inputHold;
    private readonly HashSet<int> swallowedKeyUps = new();
    private readonly HashSet<int> swallowedMouseUps = new();
    private readonly HashSet<int> downKeys = new();
    private readonly HashSet<int> downModifiers = new();
    /// Set once a shortcut used the held modifiers, so releasing Win/Alt mustn't open Start or a menu bar.
    private bool maskRelease;

    // Recording a new shortcut from the settings.
    private Action<KeyCombo?>? recorder;
    private Mods recordPeak;
    private readonly HashSet<int> recordModifierKeys = new();

    private const double ArmingDelay = 0.15;

    public HotkeyMonitor()
    {
        keyboardProc = KeyboardHook;
        mouseProc = MouseHook;
    }

    private Mods CurrentMods
    {
        get
        {
            var mods = Mods.None;
            foreach (var key in downModifiers)
            {
                if (KeyCombo.ModifierKeys.TryGetValue(key, out var info)) mods |= info.Flag;
            }
            return mods;
        }
    }

    private bool HoldDown => hold is Hold.Armed or Hold.Active || inputHold != null;

    /// Returns false when the hooks cannot be installed.
    public bool Start()
    {
        if (hookThread != null) return keyboardHook != IntPtr.Zero;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            hookThread = Dispatcher.CurrentDispatcher;
            arming = new DispatcherTimer(DispatcherPriority.Send, hookThread) { Interval = TimeSpan.FromSeconds(ArmingDelay) };
            arming.Tick += (_, _) =>
            {
                arming.Stop();
                if (hold != Hold.Armed) return;
                hold = Hold.Active;
                Emit(new HotkeyEvent(HotkeyKind.HoldStart, holdAction));
            };
            var module = Native.GetModuleHandle(null);
            keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
            if (keyboardHook == IntPtr.Zero) Log.Write($"Keyboard hook failed: {Marshal.GetLastWin32Error()}");
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
            ready.Set();
            // Hook callbacks are delivered through this thread's message loop.
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Lidiflow hotkeys",
            Priority = ThreadPriority.Highest,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        // Key-ups that happen on the lock screen never reach the hooks.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        return keyboardHook != IntPtr.Zero;
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        var thread = hookThread;
        if (thread == null) return;
        thread.Invoke(() =>
        {
            if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            keyboardHook = mouseHook = IntPtr.Zero;
        });
        thread.InvokeShutdown();
        hookThread = null;
    }

    /// Captures the next shortcut pressed in the Lidiflow window; global shortcuts are off meanwhile.
    public bool Record(Action<KeyCombo?> callback)
    {
        Action<KeyCombo?>? previous = null;
        OnHookThread(() =>
        {
            previous = recorder;
            recorder = callback;
            recordPeak = Mods.None;
            recordModifierKeys.Clear();
        });
        previous?.Invoke(null);
        return true;
    }

    public void CancelRecording()
    {
        Action<KeyCombo?>? callback = null;
        OnHookThread(() =>
        {
            callback = recorder;
            recorder = null;
        });
        callback?.Invoke(null);
    }

    private void OnHookThread(Action action)
    {
        if (hookThread == null) action(); else hookThread.Invoke(action);
    }

    private void Emit(HotkeyEvent e) => ui.InvokeAsync(() => OnAction(e));

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => hookThread?.InvokeAsync(ResetState);

    /// Forgets every key believed to be down, ending any hold.
    private void ResetState()
    {
        if (hold == Hold.Active || inputHold != null) Emit(new HotkeyEvent(HotkeyKind.HoldEnd));
        arming.Stop();
        hold = Hold.None;
        inputHold = null;
        modifierTap = null;
        maskRelease = false;
        downModifiers.Clear();
        downKeys.Clear();
        swallowedKeyUps.Clear();
        swallowedMouseUps.Clear();
    }

    /// Drops modifiers whose key-up never arrived (secure desktop, elevated windows), so a stuck Win
    /// doesn't turn every Ctrl press into a dictation. Only while idle: during a hold, the app's own
    /// synthetic key-ups (KeySender.CtrlChord) make Windows report keys that are still held as up.
    private void DropReleasedModifiers(int vk)
    {
        if (recorder != null || inputHold != null || hold is Hold.Armed or Hold.Active) return;
        if (downModifiers.RemoveWhere(key => key != vk && (Native.GetAsyncKeyState(key) & 0x8000) == 0) == 0) return;
        modifierTap = null;
        if (hold == Hold.Consumed && (CurrentMods & consumedFlags) != consumedFlags) hold = Hold.None;
    }

    /// Recording only listens to the Lidiflow window: a key pressed in another app ends it, untouched.
    private void StopRecordingOutsideApp()
    {
        if (recorder == null) return;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
        if (pid == (uint)Environment.ProcessId) return;
        var callback = recorder;
        recorder = null;
        recordModifierKeys.Clear();
        ui.InvokeAsync(() => callback(null));
    }

    // MARK: - Hooks

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                int message = (int)wParam;
                bool down = message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool up = message is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                // Our own synthetic input (paste, masked releases) goes straight through.
                if ((info.flags & Native.LLKHF_INJECTED) == 0 && (down || up) && !HandleKey((int)info.vkCode, down))
                {
                    return (IntPtr)1;
                }
            }
            catch (Exception e)
            {
                Log.Write("Keyboard hook: " + e);
            }
        }
        return Native.CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                int message = (int)wParam;
                int? button = message switch
                {
                    Native.WM_MBUTTONDOWN or Native.WM_MBUTTONUP => 2,
                    Native.WM_XBUTTONDOWN or Native.WM_XBUTTONUP => (info.mouseData >> 16) == 1 ? 3 : 4,
                    _ => null,
                };
                if (button is int b && (info.flags & Native.LLMHF_INJECTED) == 0)
                {
                    bool down = message is Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN;
                    if (!HandleMouse(b, down)) return (IntPtr)1;
                }
            }
            catch (Exception e)
            {
                Log.Write("Mouse hook: " + e);
            }
        }
        return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    /// Returns true to let the event through, false to swallow it.
    private bool HandleKey(int vk, bool down)
    {
        StopRecordingOutsideApp();
        DropReleasedModifiers(vk);
        if (KeyCombo.ModifierKeys.TryGetValue(vk, out var modifier))
        {
            var before = CurrentMods;
            if (down) downModifiers.Add(vk); else downModifiers.Remove(vk);
            var after = CurrentMods;

            if (recorder != null) return RecordModifier(vk, down, after);

            if (after != before) ModifiersChanged(after, vk);

            if (!down && maskRelease && modifier.Flag is Mods.Win or Mods.Alt)
            {
                if (after == Mods.None) maskRelease = false;
                KeySender.MaskedRelease(vk);
                return false;
            }
            if (after == Mods.None) maskRelease = false;
            return true;
        }

        if (down)
        {
            bool repeat = !downKeys.Add(vk);
            if (recorder != null) return RecordKey(vk, repeat);
            if (repeat) return !swallowedKeyUps.Contains(vk);
            bool pass = KeyDown(vk, CurrentMods);
            if (!pass && CurrentMods != Mods.None) maskRelease = true;
            return pass;
        }

        downKeys.Remove(vk);
        if (inputHold is { } held && held.Key == vk)
        {
            inputHold = null;
            Emit(new HotkeyEvent(HotkeyKind.HoldEnd));
        }
        return !swallowedKeyUps.Remove(vk);
    }

    private bool HandleMouse(int button, bool down)
    {
        StopRecordingOutsideApp();
        if (down)
        {
            if (recorder != null)
            {
                FinishRecording(KeyCombo.Mouse(button, CurrentMods));
                swallowedMouseUps.Add(button);
                return false;
            }
            bool pass = MouseDown(button, CurrentMods);
            if (!pass && CurrentMods != Mods.None) maskRelease = true;
            return pass;
        }
        if (inputHold is { } held && held.Mouse == button)
        {
            inputHold = null;
            Emit(new HotkeyEvent(HotkeyKind.HoldEnd));
        }
        return !swallowedMouseUps.Remove(button);
    }

    // MARK: - Modifiers alone

    private void ModifiersChanged(Mods flags, int key)
    {
        switch (hold)
        {
            case Hold.Armed:
            case Hold.Active:
                var combo = holdCombo!;
                if ((flags & combo.Modifiers) != combo.Modifiers)
                {
                    ReleaseModifierHold();
                }
                else if (flags != combo.Modifiers && holdAction == ShortcutAction.Dictate
                         && ModifierCombo(ShortcutAction.Command, flags, key) is { } command)
                {
                    // Adding a modifier to the dictation hold turns it into command mode.
                    holdAction = ShortcutAction.Command;
                    holdCombo = command;
                    if (hold == Hold.Active) Emit(new HotkeyEvent(HotkeyKind.SwitchToCommand));
                }
                return;
            case Hold.Consumed:
                if ((flags & consumedFlags) != consumedFlags) hold = Hold.None;
                return;
        }

        if (modifierTap is { } tap)
        {
            if ((flags & tap.Combo.Modifiers) != tap.Combo.Modifiers)
            {
                modifierTap = null;
                Fire(tap.Action);
            }
            else if (flags != tap.Combo.Modifiers)
            {
                modifierTap = null;
            }
            return;
        }
        if (flags == Mods.None) return;

        foreach (var action in new[] { ShortcutAction.Command, ShortcutAction.Dictate })
        {
            if (ModifierCombo(action, flags, key) is { } match)
            {
                PressHold(action, match);
                return;
            }
        }
        foreach (var action in new[] { ShortcutAction.HandsFree, ShortcutAction.PasteLast, ShortcutAction.CopyLast, ShortcutAction.Cancel })
        {
            if (ModifierCombo(action, flags, key) is { } match)
            {
                modifierTap = (action, match);
                maskRelease = true;
                return;
            }
        }
    }

    private KeyCombo? ModifierCombo(ShortcutAction action, Mods flags, int key) =>
        CombosFor(action).FirstOrDefault(combo => combo.MatchesModifiers(flags, key));

    private List<KeyCombo> CombosFor(ShortcutAction action) =>
        Shortcuts.TryGetValue(action, out var combos) ? combos : new List<KeyCombo>();

    private void PressHold(ShortcutAction action, KeyCombo combo)
    {
        maskRelease = true;
        if (action == ShortcutAction.Dictate && IsHandsFree())
        {
            hold = Hold.Consumed;
            consumedFlags = combo.Modifiers;
            Emit(new HotkeyEvent(HotkeyKind.Stop));
            return;
        }
        hold = Hold.Armed;
        holdAction = action;
        holdCombo = combo;
        arming.Stop();
        arming.Start();
    }

    private void ReleaseModifierHold()
    {
        if (hold == Hold.Active) Emit(new HotkeyEvent(HotkeyKind.HoldEnd));
        arming.Stop();
        hold = Hold.None;
    }

    /// Ends a modifier hold because another shortcut used it.
    private void ConsumeModifierHold(bool discarding)
    {
        switch (hold)
        {
            case Hold.Armed:
                arming.Stop();
                hold = Hold.Consumed;
                consumedFlags = holdCombo!.Modifiers;
                break;
            case Hold.Active:
                if (discarding) Emit(new HotkeyEvent(HotkeyKind.Discard));
                hold = Hold.Consumed;
                consumedFlags = holdCombo!.Modifiers;
                break;
        }
    }

    // MARK: - Keys and mouse buttons

    private bool KeyDown(int code, Mods flags)
    {
        modifierTap = null;
        var match = ShortcutActions.All.Cast<ShortcutAction?>()
            .FirstOrDefault(action => CombosFor(action!.Value).Any(combo => combo.Matches(code, flags)));
        if (match is { } action && Trigger(action, code, null))
        {
            swallowedKeyUps.Add(code);
            return false;
        }
        if (TransformIndex(code, flags, HoldDown) is int index)
        {
            ConsumeModifierHold(discarding: !IsHandsFree());
            swallowedKeyUps.Add(code);
            Emit(new HotkeyEvent(HotkeyKind.Transform, Index: index));
            return false;
        }
        // The held modifiers belong to some other shortcut (Ctrl+C, Win+→…): drop the dictation.
        ConsumeModifierHold(discarding: true);
        return true;
    }

    private bool MouseDown(int button, Mods flags)
    {
        modifierTap = null;
        var match = ShortcutActions.All.Cast<ShortcutAction?>()
            .FirstOrDefault(action => CombosFor(action!.Value).Any(combo => combo.MatchesMouse(button, flags)));
        if (match is not { } action || !Trigger(action, null, button)) return true;
        swallowedMouseUps.Add(button);
        return false;
    }

    /// Runs a shortcut pressed with a key or mouse button. Returns false to let the input through.
    private bool Trigger(ShortcutAction action, int? keyCode, int? mouseButton)
    {
        switch (action)
        {
            case ShortcutAction.Cancel:
                if (!IsRecording() && !HoldDown) return false;
                ConsumeModifierHold(discarding: false);
                Emit(new HotkeyEvent(HotkeyKind.Cancel));
                break;
            case ShortcutAction.HandsFree:
                // Pressed during a dictation hold, it locks that dictation hands-free.
                ConsumeModifierHold(discarding: !(hold == Hold.Active && holdAction == ShortcutAction.Dictate));
                Emit(new HotkeyEvent(HotkeyKind.HandsFree));
                break;
            case ShortcutAction.Dictate:
            case ShortcutAction.Command:
                ConsumeModifierHold(discarding: true);
                if (action == ShortcutAction.Dictate && IsHandsFree())
                {
                    Emit(new HotkeyEvent(HotkeyKind.Stop));
                }
                else
                {
                    inputHold = (action, keyCode, mouseButton);
                    Emit(new HotkeyEvent(HotkeyKind.HoldStart, action));
                }
                break;
            case ShortcutAction.PasteLast:
            case ShortcutAction.CopyLast:
                ConsumeModifierHold(discarding: true);
                Fire(action);
                break;
        }
        return true;
    }

    private void Fire(ShortcutAction action)
    {
        switch (action)
        {
            case ShortcutAction.HandsFree: Emit(new HotkeyEvent(HotkeyKind.HandsFree)); break;
            case ShortcutAction.PasteLast: Emit(new HotkeyEvent(HotkeyKind.PasteLast)); break;
            case ShortcutAction.CopyLast: Emit(new HotkeyEvent(HotkeyKind.CopyLast)); break;
            case ShortcutAction.Cancel: if (IsRecording()) Emit(new HotkeyEvent(HotkeyKind.Cancel)); break;
        }
    }

    // MARK: - Recording

    /// While recording, every key is swallowed so Win doesn't open Start and nothing reaches the app.
    private bool RecordModifier(int vk, bool down, Mods after)
    {
        if (down)
        {
            recordPeak |= after;
            recordModifierKeys.Add(vk);
            return false;
        }
        if (after == Mods.None && recordPeak != Mods.None)
        {
            int? side = recordModifierKeys.Count == 1 ? recordModifierKeys.First() : null;
            FinishRecording(KeyCombo.ModifiersOnly(recordPeak, side));
        }
        return false;
    }

    private bool RecordKey(int vk, bool repeat)
    {
        if (!repeat)
        {
            FinishRecording(KeyCombo.Key(vk, CurrentMods));
            swallowedKeyUps.Add(vk);
        }
        return false;
    }

    private void FinishRecording(KeyCombo combo)
    {
        var callback = recorder;
        recorder = null;
        // Modifiers still held after a key combo were swallowed on the way down; mask their release too.
        maskRelease = CurrentMods != Mods.None;
        if (callback != null) ui.InvokeAsync(() => callback(combo));
    }
}
