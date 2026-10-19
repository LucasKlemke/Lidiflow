using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lidiflow.Platform;

namespace Lidiflow.UI;

public enum OverlayPhase { Hidden, Listening, HandsFree, Command, Transcribing, Polishing, Thinking, Transforming, Error, Tip }

/// The dictation bar, docked at the bottom or on a side of the monitor under the mouse. Idle, it is a small handle
/// that grows on hover into a "Ditar" button; while dictating it shows the waveform with cancel and finish buttons;
/// then the progress. It never takes focus (WS_EX_NOACTIVATE), so the paste lands in the app being dictated into.
/// Dragging it shows the three places it can dock and snaps to the nearest (see DockGuideWindow).
public partial class OverlayWindow : Window
{
    public static class Edges
    {
        public const string Bottom = "bottom", Left = "left", Right = "right";
        public static readonly string[] All = { Bottom, Left, Right };
        public static string Title(string edge) => edge switch { Left => "Esquerda", Right => "Direita", _ => "Embaixo" };
    }

    private static readonly double[] Weights = { 0.5, 0.8, 1, 0.8, 0.5 };
    private static readonly Color WhiteColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color PillColor = Color.FromRgb(0x14, 0x14, 0x14);
    private static readonly Color AccentColor = Color.FromRgb(0x1F, 0x6B, 0xFF);
    private static readonly Brush White = Frozen(WhiteColor);
    private static readonly Brush Accent = Frozen(AccentColor);
    // Waveform: blue while dictating, orange for a voice command.
    private static readonly Brush Blue = Frozen(Color.FromRgb(0x4D, 0x94, 0xFF));
    private static readonly Brush Orange = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush Warning = Frozen(Color.FromRgb(0xFF, 0xD6, 0x0A));
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private const double DragThreshold = 4;
    private const double MarginFromEdge = 12;

    // Sizes in DIPs, as on the Mac.
    private static readonly Size Idle = new(44, 8), IdleSide = new(8, 44);
    private static readonly Size Hovered = new(168, 36), HoveredSide = new(52, 112);
    private static readonly Size Recording = new(300, 40), RecordingSide = new(40, 104);
    private static readonly Size Busy = new(260, 40), BusySide = new(40, 40), TipSize = new(380, 40);

    private readonly DockGuideWindow guide = new();
    private readonly DispatcherTimer screenTimer = new() { Interval = TimeSpan.FromSeconds(0.5) };
    private readonly Rectangle[] bars = new Rectangle[5];
    private IntPtr handle;
    private OverlayPhase phase = OverlayPhase.Hidden;
    private string? detail;
    private bool hovering;
    private DateTime? hiddenUntil;
    private IntPtr currentMonitor;
    private IReadOnlyList<string> dictateKeys = Array.Empty<string>();
    private bool showsIdleBar = true;
    private string edge = Edges.Bottom;

    // Dragging, in physical pixels.
    private Native.POINT? pressPoint;
    private Native.POINT dragOffset;
    private bool dragging;

    // Wired by AppController.
    public Action OnDictate { get; set; } = () => { };
    public Action OnStop { get; set; } = () => { };
    public Action OnCancel { get; set; } = () => { };
    public Action OnPasteLast { get; set; } = () => { };
    public Action OnOpenApp { get; set; } = () => { };
    public Func<string> CurrentMicrophone { get; set; } = () => Microphones.Automatic;
    public Action<string> OnSelectMicrophone { get; set; } = _ => { };
    public Action<string> OnEdgeChanged { get; set; } = _ => { };

    public OverlayWindow()
    {
        InitializeComponent();
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i] = new Rectangle { Width = 3, Height = 4, RadiusX = 1, RadiusY = 1, VerticalAlignment = VerticalAlignment.Center };
        }
        handle = new WindowInteropHelper(this).EnsureHandle();
        var style = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
        Native.SetWindowLong(handle, Native.GWL_EXSTYLE, style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);

        Root.MouseEnter += (_, _) => SetHovering(true);
        Root.MouseLeave += (_, _) => SetHovering(false);
        Root.PreviewMouseLeftButtonDown += OnPress;
        Root.PreviewMouseMove += OnMove;
        Root.PreviewMouseLeftButtonUp += OnRelease;
        Root.LostMouseCapture += (_, _) => { if (dragging) EndDrag(); };
        Root.ContextMenuOpening += (_, _) => Root.ContextMenu = BuildMenu();
        Root.ContextMenu = BuildMenu();
        DpiChanged += (_, _) => Place();
        // With several monitors the bar sits on the one with the mouse, and moves when the mouse does.
        screenTimer.Tick += (_, _) =>
        {
            if (phase == OverlayPhase.Hidden && !dragging && MouseMonitor().Monitor != currentMonitor) Place();
        };
        screenTimer.Start();
        Render();
    }

    private static Brush Frozen(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    // MARK: - State

    /// Microphone RMS level; drives the waveform.
    public float Level
    {
        set
        {
            var level = Math.Min(value * 12, 1);
            for (int i = 0; i < bars.Length; i++) bars[i].Height = 4 + 18 * level * Weights[i];
        }
    }

    public IReadOnlyList<string> DictateKeys
    {
        set { dictateKeys = value; if (phase == OverlayPhase.Hidden && hovering) Render(); }
    }

    /// Off in Settings, the bar only appears while dictating, as the indicator.
    public bool ShowsIdleBar
    {
        set { showsIdleBar = value; Place(); }
    }

    public string Edge
    {
        get => edge;
        set { edge = Edges.All.Contains(value) ? value : Edges.Bottom; Render(); Place(); }
    }

    private bool Side => edge != Edges.Bottom;

    public void Display(OverlayPhase newPhase, string? newDetail = null)
    {
        phase = newPhase;
        detail = newDetail;
        if (phase != OverlayPhase.Hidden) hovering = false;
        Render();
        Place();
    }

    private void SetHovering(bool value)
    {
        if (phase != OverlayPhase.Hidden || dragging || hovering == value) return;
        hovering = value;
        Render();
        Place();
    }

    private string Label => phase switch
    {
        OverlayPhase.Listening => "Ouvindo…",
        OverlayPhase.HandsFree => "Mãos livres",
        OverlayPhase.Command => "Diga o que fazer…",
        OverlayPhase.Thinking => "Pensando…",
        OverlayPhase.Transcribing => "Transcrevendo…",
        OverlayPhase.Polishing => "Ajustando texto…",
        OverlayPhase.Transforming => $"✨ {detail}…",
        OverlayPhase.Error or OverlayPhase.Tip => detail ?? "",
        _ => "",
    };

    private bool IsRecording => phase is OverlayPhase.Listening or OverlayPhase.HandsFree or OverlayPhase.Command;

    /// The bar's size for its state, or null when it should not be on screen.
    private Size? CurrentSize()
    {
        if (phase == OverlayPhase.Hidden)
        {
            bool snoozed = hiddenUntil is { } until && until > DateTime.Now;
            if (!showsIdleBar || snoozed) return null;
            if (hovering) return Side ? HoveredSide : Hovered;
            return Side ? IdleSide : Idle;
        }
        if (IsRecording) return Side ? RecordingSide : Recording;
        if (phase == OverlayPhase.Tip) return Side ? BusySide : TipSize;
        return Side ? BusySide : Busy;
    }

    // MARK: - Content

    private void Render()
    {
        bool idleHandle = phase == OverlayPhase.Hidden && !hovering;
        Root.Background = Frozen(PillColor, idleHandle ? 0.5 : 0.82);
        Root.BorderBrush = Frozen(WhiteColor, idleHandle ? 0.25 : 0.14);
        Root.ToolTip = null;
        foreach (var bar in bars)
        {
            (bar.Parent as Panel)?.Children.Remove(bar);
            bar.Fill = phase == OverlayPhase.Command ? Orange : Blue;
        }

        if (phase == OverlayPhase.Hidden)
        {
            Root.Child = hovering ? (Side ? IdleSideContent() : IdleContent()) : null;
        }
        else if (IsRecording)
        {
            Root.Child = Side ? RecordingSideContent() : RecordingContent();
        }
        else
        {
            Root.Child = Side ? StatusIcon() : BusyContent();
            if (Side) Root.ToolTip = Label;
        }
    }

    private UIElement IdleContent()
    {
        var row = new DockPanel { Margin = new Thickness(12, 0, 5, 0), LastChildFill = false };
        var mic = ActionButton("", Accent, "Ditar sem segurar a tecla", () => OnDictate());
        DockPanel.SetDock(mic, Dock.Right);
        row.Children.Add(mic);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Text("Ditar", 13));
        if (dictateKeys.Count > 0) left.Children.Add(KeyCaps(new Thickness(8, 0, 0, 0)));
        row.Children.Add(left);
        return row;
    }

    private UIElement IdleSideContent()
    {
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var label = Text("Ditar", 11);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(label);
        if (dictateKeys.Count > 0) column.Children.Add(KeyCaps(new Thickness(0, 6, 0, 0)));
        var mic = ActionButton("", Accent, "Ditar sem segurar a tecla", () => OnDictate());
        mic.Margin = new Thickness(0, 6, 0, 0);
        column.Children.Add(mic);
        return column;
    }

    private UIElement RecordingContent()
    {
        var row = new DockPanel { Margin = new Thickness(6, 0, 6, 0), LastChildFill = true };
        var cancel = ActionButton("", Frozen(WhiteColor, 0.12), "Cancelar", () => OnCancel());
        var stop = ActionButton("", Accent, "Finalizar e colar", () => OnStop());
        DockPanel.SetDock(cancel, Dock.Left);
        DockPanel.SetDock(stop, Dock.Right);
        row.Children.Add(cancel);
        row.Children.Add(stop);
        var middle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        middle.Children.Add(Waveform());
        var label = Text(Label, 13);
        label.Margin = new Thickness(10, 0, 0, 0);
        middle.Children.Add(label);
        row.Children.Add(middle);
        return row;
    }

    private UIElement RecordingSideContent()
    {
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        column.Children.Add(ActionButton("", Frozen(WhiteColor, 0.12), "Cancelar", () => OnCancel()));
        var wave = Waveform();
        wave.Margin = new Thickness(0, 8, 0, 8);
        wave.ToolTip = Label;
        column.Children.Add(wave);
        column.Children.Add(ActionButton("", Accent, "Finalizar e colar", () => OnStop()));
        return column;
    }

    private UIElement BusyContent()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(StatusIcon());
        var label = Text(Label, 13);
        label.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(label);
        return row;
    }

    private FrameworkElement StatusIcon()
    {
        if (phase == OverlayPhase.Error) return Glyph("", Warning, 15);
        if (phase == OverlayPhase.Tip) return Glyph("", White, 15);
        var spinner = new System.Windows.Shapes.Path
        {
            Width = 16, Height = 16, Stroke = White, StrokeThickness = 2, Stretch = Stretch.None,
            Data = Geometry.Parse("M 8,1 A 7,7 0 1 1 1,8"), RenderTransformOrigin = new Point(0.5, 0.5),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var rotation = new RotateTransform();
        spinner.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        return spinner;
    }

    private Panel Waveform()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i].Margin = new Thickness(0, 0, i < bars.Length - 1 ? 3 : 0, 0);
            panel.Children.Add(bars[i]);
        }
        return panel;
    }

    private static TextBlock Text(string text, double size) => new()
    {
        Text = text, FontSize = size, FontWeight = FontWeights.Medium, Foreground = White,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static TextBlock Glyph(string glyph, Brush color, double size) => new()
    {
        Text = glyph, FontFamily = Icons, FontSize = size, Foreground = color,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private UIElement KeyCaps(Thickness margin)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in dictateKeys)
        {
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 3, 0),
                Background = Frozen(WhiteColor, 0.18), BorderBrush = Frozen(WhiteColor, 0.35), BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = key, FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
                    FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = White,
                },
            });
        }
        return row;
    }

    /// A 26×26 button with 6 pt corners (concentric with the bar's 10). A press that turns into a drag never clicks.
    private Border ActionButton(string glyph, Brush fill, string help, Action action)
    {
        var button = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(6), Background = fill, Cursor = Cursors.Hand,
            ToolTip = help, Child = Glyph(glyph, White, 11), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, help);
        bool pressed = false;
        button.MouseLeftButtonDown += (_, e) => { pressed = true; e.Handled = true; };
        button.MouseLeave += (_, _) => pressed = false;
        button.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed || dragging) return;
            pressed = false;
            e.Handled = true;
            action();
        };
        return button;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Colar a última transcrição", () => OnPasteLast()));

        var microphone = new MenuItem { Header = "Microfone" };
        var current = CurrentMicrophone();
        microphone.Items.Add(Item("Automático", () => OnSelectMicrophone(Microphones.Automatic), current == Microphones.Automatic));
        microphone.Items.Add(new Separator());
        foreach (var device in Microphones.Inputs())
        {
            var id = device.Id;
            microphone.Items.Add(Item(device.Name, () => OnSelectMicrophone(id), current == id));
        }
        menu.Items.Add(microphone);

        var position = new MenuItem { Header = "Posição" };
        foreach (var option in Edges.All)
        {
            position.Items.Add(Item(Edges.Title(option), () => DockAt(option), edge == option));
        }
        menu.Items.Add(position);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Esconder por 1 hora", () => HideFor(TimeSpan.FromHours(1))));
        menu.Items.Add(Item("Abrir o Lidiflow", () => OnOpenApp()));
        return menu;
    }

    private static MenuItem Item(string header, Action action, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    // MARK: - Placement

    private readonly record struct MonitorArea(IntPtr Monitor, Native.RECT Work, double Scale);

    private static MonitorArea MouseMonitor()
    {
        Native.GetCursorPos(out var cursor);
        var monitor = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        double scale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        return new MonitorArea(monitor, info.rcWork, scale);
    }

    /// Bottom center, or the middle of a side, 12 pt from the edge, in physical pixels.
    private static Rect Slot(string edge, Size size, MonitorArea area)
    {
        double w = Math.Round(size.Width * area.Scale), h = Math.Round(size.Height * area.Scale), m = Math.Round(MarginFromEdge * area.Scale);
        var work = area.Work;
        double midX = (work.left + work.right) / 2.0, midY = (work.top + work.bottom) / 2.0;
        return edge switch
        {
            Edges.Left => new Rect(work.left + m, Math.Round(midY - h / 2), w, h),
            Edges.Right => new Rect(work.right - w - m, Math.Round(midY - h / 2), w, h),
            _ => new Rect(Math.Round(midX - w / 2), work.bottom - h - m, w, h),
        };
    }

    /// Re-anchors the bar on the monitor under the mouse.
    public void Place()
    {
        if (dragging) return;
        if (CurrentSize() is not { } size)
        {
            Hide();
            return;
        }
        var area = MouseMonitor();
        currentMonitor = area.Monitor;
        var rect = Slot(edge, size, area);
        if (!IsVisible) Show();
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private void HideFor(TimeSpan duration)
    {
        hiddenUntil = DateTime.Now + duration;
        hovering = false;
        Place();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            hiddenUntil = null;
            Render();
            Place();
        };
        timer.Start();
    }

    private void DockAt(string newEdge)
    {
        Edge = newEdge;
        OnEdgeChanged(edge);
    }

    // MARK: - Dragging

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        Native.GetCursorPos(out var cursor);
        pressPoint = cursor;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        Native.GetCursorPos(out var cursor);
        var area = MouseMonitor();
        if (!dragging)
        {
            if (Math.Abs(cursor.x - start.x) < DragThreshold * area.Scale && Math.Abs(cursor.y - start.y) < DragThreshold * area.Scale) return;
            Native.GetWindowRect(handle, out var window);
            dragOffset = new Native.POINT { x = start.x - window.left, y = start.y - window.top };
            dragging = true;
            Root.CaptureMouse();
        }
        Native.GetWindowRect(handle, out var current);
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, cursor.x - dragOffset.x, cursor.y - dragOffset.y,
            current.right - current.left, current.bottom - current.top, Native.SWP_NOACTIVATE);
        // Each place is drawn the size of the opened bar there.
        var slots = Edges.All.ToDictionary(place => place, place => Slot(place, place == Edges.Bottom ? Hovered : HoveredSide, area));
        guide.ShowSlots(area.Work, area.Scale, NearestEdge(cursor, area), slots);
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        pressPoint = null;
        if (!dragging) return;
        e.Handled = true;
        EndDrag();
    }

    private void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        pressPoint = null;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        guide.Hide();
        Native.GetCursorPos(out var cursor);
        var target = NearestEdge(cursor, MouseMonitor());
        AnimateTo(target);
    }

    /// Slides from where it was dropped to its dock place, then saves the place.
    private void AnimateTo(string target)
    {
        edge = target;
        OnEdgeChanged(edge);
        Render();
        if (CurrentSize() is not { } size) { Place(); return; }
        var area = MouseMonitor();
        var to = Slot(edge, size, area);
        Native.GetWindowRect(handle, out var from);
        var started = DateTime.Now;
        var duration = TimeSpan.FromMilliseconds(180);
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.Now - started).TotalMilliseconds / duration.TotalMilliseconds);
            double ease = 1 - Math.Pow(1 - t, 3);
            int x = (int)Math.Round(from.left + (to.X - from.left) * ease), y = (int)Math.Round(from.top + (to.Y - from.top) * ease);
            Native.SetWindowPos(handle, Native.HWND_TOPMOST, x, y, (int)to.Width, (int)to.Height, Native.SWP_NOACTIVATE);
            if (t < 1) return;
            timer.Stop();
            Place();
        };
        timer.Start();
    }

    /// The dock place whose slot is closest to the cursor.
    private static string NearestEdge(Native.POINT cursor, MonitorArea area) =>
        Edges.All.OrderBy(candidate =>
        {
            var slot = Slot(candidate, candidate == Edges.Bottom ? Idle : IdleSide, area);
            double dx = slot.X + slot.Width / 2 - cursor.x, dy = slot.Y + slot.Height / 2 - cursor.y;
            return dx * dx + dy * dy;
        }).First();
}

/// While the bar is dragged: the three places it can dock, the one it will snap to highlighted. Click-through.
internal sealed class DockGuideWindow : Window
{
    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color Accent = Color.FromRgb(0x1F, 0x6B, 0xFF);
    private readonly Canvas canvas = new();
    private readonly IntPtr handle;

    public DockGuideWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Content = canvas;
        handle = new WindowInteropHelper(this).EnsureHandle();
        var style = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
        Native.SetWindowLong(handle, Native.GWL_EXSTYLE,
            style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
    }

    /// `slots` are each place's rectangle in physical pixels; the canvas draws them in DIPs over the work area.
    internal void ShowSlots(Native.RECT work, double scale, string nearest, IReadOnlyDictionary<string, Rect> slots)
    {
        canvas.Children.Clear();
        foreach (var edge in OverlayWindow.Edges.All)
        {
            var rect = slots[edge];
            bool active = edge == nearest;
            var marker = new Border
            {
                Width = rect.Width / scale, Height = rect.Height / scale, CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(active ? Accent : Color.FromRgb(0x14, 0x14, 0x14)) { Opacity = active ? 0.28 : 0.18 },
            };
            var outline = new Rectangle
            {
                Width = marker.Width, Height = marker.Height, RadiusX = 10, RadiusY = 10,
                Stroke = new SolidColorBrush(active ? Accent : White) { Opacity = active ? 1 : 0.6 },
                StrokeThickness = active ? 2 : 1, StrokeDashArray = active ? null : new DoubleCollection { 4, 3 },
            };
            var left = (rect.X - work.left) / scale;
            var top = (rect.Y - work.top) / scale;
            Canvas.SetLeft(marker, left);
            Canvas.SetTop(marker, top);
            Canvas.SetLeft(outline, left);
            Canvas.SetTop(outline, top);
            canvas.Children.Add(marker);
            canvas.Children.Add(outline);
        }
        if (!IsVisible) Show();
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, work.left, work.top, work.right - work.left, work.bottom - work.top,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }
}
