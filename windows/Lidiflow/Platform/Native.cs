using System;
using System.Runtime.InteropServices;

namespace Lidiflow.Platform;

/// Win32 calls for global hooks, synthetic input, the clipboard and window styles.
internal static class Native
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int WM_XBUTTONUP = 0x020C;

    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLMHF_INJECTED = 0x01;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Synthetic input

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // Window styles

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // Placement in physical pixels, on the monitor under the mouse

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    /// MDT_EFFECTIVE_DPI = 0.
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}

/// Sends key presses to the focused app.
internal static class KeySender
{
    public const int VK_CONTROL = 0x11;
    /// An unassigned key: pressing it between Win/Alt down and up stops the Start menu or menu bar from opening.
    public const int MaskKey = 0xE8;

    private static readonly int[] OtherModifiers = { 0x5B, 0x5C, 0xA4, 0xA5, 0xA0, 0xA1 };

    /// Ctrl + key, releasing Win/Alt/Shift first so a still-held shortcut doesn't turn Ctrl+V into Ctrl+Win+V.
    public static void CtrlChord(int vk)
    {
        var inputs = new System.Collections.Generic.List<Native.INPUT>();
        var held = Array.FindAll(OtherModifiers, key => (Native.GetAsyncKeyState(key) & 0x8000) != 0);
        if (Array.Exists(held, key => key is 0x5B or 0x5C or 0xA4 or 0xA5))
        {
            inputs.Add(Key(MaskKey, up: false));
            inputs.Add(Key(MaskKey, up: true));
        }
        foreach (var key in held) inputs.Add(Key(key, up: true));
        inputs.Add(Key(VK_CONTROL, up: false));
        inputs.Add(Key(vk, up: false));
        inputs.Add(Key(vk, up: true));
        inputs.Add(Key(VK_CONTROL, up: true));
        Send(inputs.ToArray());
    }

    /// Releases a Win or Alt key without opening the Start menu or the app's menu bar.
    public static void MaskedRelease(int vk) =>
        Send(Key(MaskKey, up: false), Key(MaskKey, up: true), Key(vk, up: true));

    public static void Send(params Native.INPUT[] inputs) =>
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());

    public static Native.INPUT Key(int vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = (ushort)vk,
                dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? Native.KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    // Win, right Ctrl/Alt and Insert (without the flag, Insert would be the numpad's).
    private static bool IsExtended(int vk) => vk is 0x5B or 0x5C or 0xA3 or 0xA5 or 0x2D;
}
