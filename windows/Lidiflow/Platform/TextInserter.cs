using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using Lidiflow.Core;

namespace Lidiflow.Platform;

/// Reads the selection from, and pastes text into, the focused app. Must run on the UI thread.
///
/// Text the app puts on the clipboard is kept out of Windows' clipboard history (Win+V) and cloud sync.
public static class TextInserter
{
    public enum Target { Editable, NotEditable, Unknown }

    /// Control types that certainly take no typed text: pasting there does nothing, or something unwanted
    /// (in Explorer, Ctrl+V copies files). Panes, groups and documents of web/Electron apps are left out on purpose.
    private static readonly HashSet<ControlType> NonTextTypes = new()
    {
        ControlType.Button, ControlType.List, ControlType.ListItem, ControlType.Tree, ControlType.TreeItem,
        ControlType.Image, ControlType.Text, ControlType.Hyperlink, ControlType.CheckBox, ControlType.RadioButton,
        ControlType.Slider, ControlType.Menu, ControlType.MenuItem, ControlType.MenuBar, ControlType.ToolBar,
        ControlType.Tab, ControlType.TabItem, ControlType.Table, ControlType.DataGrid,
    };

    /// Whether the focused element takes text, so a dictation is pasted only where it lands somewhere. Asks UI
    /// Automation off the UI thread with a short timeout; when it cannot tell, says so, and the caller pastes as before
    /// rather than lose a dictation (browsers and Electron apps often don't answer).
    public static async Task<Target> FocusedTargetAsync()
    {
        var query = Task.Run(() =>
        {
            bool explorer = ForegroundProcessName() == "explorer";
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return explorer ? Target.NotEditable : Target.Unknown;
            var type = focused.Current.ControlType;
            if (type == ControlType.Edit || type == ControlType.Document) return Target.Editable;
            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern value && !value.Current.IsReadOnly && !NonTextTypes.Contains(type))
            {
                return Target.Editable;
            }
            // Explorer only takes text in real fields (rename, address, search); anywhere else Ctrl+V copies files.
            return NonTextTypes.Contains(type) || explorer ? Target.NotEditable : Target.Unknown;
        });
        try
        {
            var finished = await Task.WhenAny(query, Task.Delay(300));
            return finished == query ? await query : Target.Unknown;
        }
        catch (Exception e)
        {
            Log.Write("Focused element: " + e.Message);
            return Target.Unknown;
        }
    }

    private static string? ForegroundProcessName()
    {
        try
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private const int VK_V = 0x56;
    private const int VK_INSERT = 0x2D;

    /// One clipboard round trip at a time, so a second paste can't snapshot the first one's text.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// Pastes text, then restores the previous clipboard.
    public static async Task InsertAsync(string text)
    {
        await Gate.WaitAsync();
        try
        {
            var saved = Snapshot();
            if (!Retry(() => Clipboard.SetDataObject(Private(new DataObject(DataFormats.UnicodeText, text)), true)))
            {
                throw new UserFacingException("Não foi possível usar a área de transferência");
            }
            var ourChange = Native.GetClipboardSequenceNumber();
            KeySender.CtrlChord(VK_V);

            await Task.Delay(600);
            // Leave the clipboard alone if something else wrote to it meanwhile.
            if (Native.GetClipboardSequenceNumber() == ourChange) Restore(saved);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// The focused app's selected text, through a Ctrl+Insert round trip. Ctrl+Insert copies like
    /// Ctrl+C, but a terminal doesn't take it as an interrupt when nothing is selected.
    public static async Task<string?> SelectedTextAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var saved = Snapshot();
            var before = Native.GetClipboardSequenceNumber();
            KeySender.CtrlChord(VK_INSERT);

            for (int i = 0; i < 8 && Native.GetClipboardSequenceNumber() == before; i++)
            {
                await Task.Delay(50);
            }
            if (Native.GetClipboardSequenceNumber() == before) return null;

            string? text = null;
            Retry(() => text = Clipboard.ContainsText() ? Clipboard.GetText() : null);
            Restore(saved);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// What was on the clipboard. Ok is false when it couldn't be read, so it mustn't be overwritten.
    private readonly record struct Saved(bool Ok, DataObject? Data);

    /// Copies every format on the clipboard so it can be put back after pasting.
    private static Saved Snapshot()
    {
        IDataObject? current = null;
        if (!Retry(() => current = Clipboard.GetDataObject())) return new Saved(false, null);
        if (current == null) return new Saved(true, null);

        string[] formats;
        try
        {
            formats = current.GetFormats(false);
        }
        catch
        {
            return new Saved(false, null);
        }
        if (formats.Length == 0) return new Saved(true, null);

        var copy = new DataObject();
        bool any = false;
        foreach (var format in formats)
        {
            try
            {
                var data = current.GetData(format, false);
                if (data == null) continue;
                copy.SetData(format, data, false);
                any = true;
            }
            catch
            {
                // Some formats (delayed rendering, metafiles) can't be read back; skip them.
            }
        }
        return any ? new Saved(true, Private(copy)) : new Saved(false, null);
    }

    private static void Restore(Saved saved)
    {
        if (!saved.Ok)
        {
            Log.Write("Clipboard was unreadable before pasting; left as is");
            return;
        }
        if (saved.Data == null)
        {
            Retry(Clipboard.Clear);
            return;
        }
        if (!Retry(() => Clipboard.SetDataObject(saved.Data, true)))
        {
            Log.Write("Could not restore the clipboard");
        }
    }

    /// Marks clipboard data so Windows' clipboard history, cloud clipboard and clipboard managers skip it.
    private static DataObject Private(DataObject data)
    {
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]), false);
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]), false);
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]), false);
        return data;
    }
    /// The clipboard is shared with every app; opening it fails while another one holds it.
    private static bool Retry(Action action)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
            catch (Exception e)
            {
                Log.Write("Clipboard: " + e.Message);
                return false;
            }
        }
        return false;
    }
}
