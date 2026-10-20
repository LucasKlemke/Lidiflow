using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lidiflow.Core;
using Lidiflow.Platform;

namespace Lidiflow.UI;

public enum OverlayPhase { Hidden, Listening, HandsFree, Command, Transcribing, Polishing, Thinking, Transforming, Error, Unplaced }

/// The dictation bar, docked at the bottom or on a side of the monitor under the mouse. Idle, it is a small handle
/// that grows on hover into a "Ditar" button; while dictating it shows the waveform with cancel and finish buttons;
/// then the progress. It never takes focus (WS_EX_NOACTIVATE), so the paste lands in the app being dictated into.
/// Dragging it shows the three places it can dock and snaps to the nearest (see DockGuideWindow).
/// While a meeting is recorded it is a pill that opens the meeting's controls on hover, beside the live transcript
/// (LivePanelWindow); detected calls and "Começou sem querer?" arrive as cards (docs/reunioes.md).
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
    private static readonly Size Hovered = new(204, 36), HoveredSide = new(40, 70);
    private static readonly Size Recording = new(300, 40), RecordingSide = new(40, 104);
    private static readonly Size Busy = new(260, 40), BusySide = new(40, 40);
    // A dictation with nowhere to paste: lying against a side edge too, like the cards below.
    private static readonly Size UnplacedSize = new(380, 124);
    // While a meeting is recorded and nothing is being dictated (docs/reunioes.md).
    private static readonly Size MeetingPill = new(112, 28), MeetingPillSide = new(32, 84);
    // The pill under the mouse: the meeting's controls, never the dictation bar.
    private static readonly Size MeetingControls = new(220, 36), MeetingControlsSide = new(40, 156);
    // Cards: a detected call arrives compact and expands. They stay lying down on a side too, against that edge:
    // their text needs to be read.
    private static readonly Size CompactSize = new(240, 40), CardSize = new(380, 64);

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
    private TimeSpan? meetingElapsed;
    private readonly List<(TextBlock Block, Func<TimeSpan, string> Format)> meetingClocks = new();
    private string edge = Edges.Bottom;
    private Card? offer;
    private bool expanded;
    private readonly DispatcherTimer offerTimer = new();
    private readonly DispatcherTimer expandTimer = new() { Interval = TimeSpan.FromSeconds(0.8) };
    private DispatcherTimer? resizeTimer;
    private readonly LivePanelWindow livePanel = new();
    private readonly HintWindow hint = new();
    /// The side bar's button under the pointer, named in the hint beside it.
    private SideHint? sideHint;
    /// Where Place last put the bar, in physical pixels, for the hint.
    private Rect placedRect;
    private double placedScale = 1;
    /// Set while the bar fades out on its way to hidden; a new state cancels it.
    private bool fadingOut;

    private enum SideHint { Dictate, Meeting }
    private bool liveOpen;
    private IReadOnlyList<string> pasteKeys = Array.Empty<string>();
    private bool copied;
    private static BitmapImage? symbol;

    /// A card shown in the bar outside dictation: a detected call, a call that ended, "Começou sem querer?".
    /// With a compact subtitle it arrives compact (240×40) and expands to the 380×64 card after 0.8 s.
    public sealed record Card(string Glyph, string Title, string Subtitle, string ActionLabel, Action Run, Action Dismissed)
    {
        /// The dot before the subtitle, if any.
        public Color? Dot { get; init; }
        public string? CompactSubtitle { get; init; }
        /// The primary button carries the Lidiflow symbol.
        public bool ActionShowsSymbol { get; init; }
        /// A quiet text button before the primary one.
        public string? SecondaryLabel { get; init; }
        public Action? Secondary { get; init; }
        /// Shows ×; without it the card only goes away by its buttons or its time.
        public bool Dismissable { get; init; } = true;
    }

    // Dragging, in physical pixels.
    private Native.POINT? pressPoint;
    private Native.POINT dragOffset;
    private bool dragging;

    // Wired by AppController.
    public Action OnDictate { get; set; } = () => { };
    public Action OnStop { get; set; } = () => { };
    public Action OnCancel { get; set; } = () => { };
    public Action OnPasteLast { get; set; } = () => { };
    public Action OnCopyUnplaced { get; set; } = () => { };
    public Action OnDismissUnplaced { get; set; } = () => { };
    public Action OnOpenApp { get; set; } = () => { };
    public Func<string> CurrentMicrophone { get; set; } = () => Microphones.Automatic;
    public Action<string> OnSelectMicrophone { get; set; } = _ => { };
    public Action<string> OnEdgeChanged { get; set; } = _ => { };
    public Action OnToggleMeeting { get; set; } = () => { };
    public Func<bool> IsMeetingRecording { get; set; } = () => false;

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
        // With several monitors the bar sits on the one with the mouse, and moves when the mouse does, recording or not.
        screenTimer.Tick += (_, _) =>
        {
            if (!dragging && MouseMonitor().Monitor != currentMonitor) Place();
        };
        screenTimer.Start();
        offerTimer.Tick += (_, _) =>
        {
            var expired = offer;
            ClearOffer();
            expired?.Dismissed();
        };
        expandTimer.Tick += (_, _) =>
        {
            expandTimer.Stop();
            if (offer != null && !expanded) Expand();
        };
        livePanel.OnClose = () => SetLiveOpen(false);
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

    /// The shortcut that pastes the last dictation, for the "nowhere to paste" card.
    public IReadOnlyList<string> PasteKeys
    {
        get => pasteKeys;
        set => pasteKeys = value;
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

    /// Time recorded in the current meeting, or null when none is: the idle bar becomes "● 12:34 ■".
    public TimeSpan? MeetingElapsed
    {
        set
        {
            bool changed = (meetingElapsed == null) != (value == null);
            meetingElapsed = value;
            livePanel.Elapsed = value ?? TimeSpan.Zero;
            if (changed)
            {
                // A new recording starts with an empty live transcript; stopping closes it.
                liveOpen = false;
                livePanel.Utterances = Array.Empty<LiveUtterance>();
                Render();
                Place();
            }
            else if (value is { } elapsed)
            {
                // Only the digits change: rebuilding the content every second would swallow a click on stop.
                foreach (var (block, format) in meetingClocks) block.Text = format(elapsed);
            }
        }
    }

    /// The meeting's live transcript so far; shown in the panel the ≡ button opens.
    public IReadOnlyList<LiveUtterance> LiveUtterances
    {
        set
        {
            livePanel.Utterances = value;
            if (liveOpen) Place();
        }
    }

    private bool ShowsMeetingPill => phase == OverlayPhase.Hidden && meetingElapsed != null;

    private bool ShowsOffer => phase == OverlayPhase.Hidden && offer != null;

    /// Windows' "Show animations" setting: off, cards appear directly at their final size.
    private static bool Animates => SystemParameters.ClientAreaAnimation;

    /// Shows a card in the bar (outside dictation) until it is used, dismissed or `lifetime` passes.
    public void ShowCard(Card card, TimeSpan lifetime)
    {
        offer = card;
        hovering = false;
        expanded = card.CompactSubtitle == null || !Animates;
        offerTimer.Stop();
        offerTimer.Interval = lifetime;
        offerTimer.Start();
        expandTimer.Stop();
        resizeTimer?.Stop();
        Render();
        Place();
        if (!Animates || phase != OverlayPhase.Hidden) return;
        SlideIn();
        if (!expanded) expandTimer.Start();
    }

    public void ClearOffer()
    {
        offerTimer.Stop();
        expandTimer.Stop();
        resizeTimer?.Stop();
        if (offer == null) return;
        offer = null;
        Render();
        Place();
    }

    /// Enters 8 pt from the edge it is docked at, fading in.
    private void SlideIn()
    {
        var shift = edge switch { Edges.Left => new Vector(-8, 0), Edges.Right => new Vector(8, 0), _ => new Vector(0, 8) };
        var move = new TranslateTransform(shift.X, shift.Y);
        Root.RenderTransform = move;
        var time = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(shift.X, 0, time) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(shift.Y, 0, time) { EasingFunction = ease });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    /// Grows the compact notice into the card on a damped spring, the card's content fading in as it opens.
    private void Expand()
    {
        expanded = true;
        Render();
        if (phase != OverlayPhase.Hidden || dragging || !Animates || Root.Child is not FrameworkElement content)
        {
            Place();
            return;
        }
        content.Opacity = 0;
        content.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(140) });
        var area = MouseMonitor();
        var started = DateTime.Now;
        var duration = TimeSpan.FromMilliseconds(450);
        resizeTimer?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        resizeTimer = timer;
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.Now - started).TotalMilliseconds / duration.TotalMilliseconds);
            double k = t >= 1 ? 1 : 1 - Math.Exp(-8 * t) * Math.Cos(7 * t);
            var size = new Size(CompactSize.Width + (CardSize.Width - CompactSize.Width) * k,
                                CompactSize.Height + (CardSize.Height - CompactSize.Height) * k);
            var rect = Slot(edge, size, area);
            Native.SetWindowPos(handle, Native.HWND_TOPMOST, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height,
                Native.SWP_NOACTIVATE);
            if (t < 1) return;
            timer.Stop();
            Place();
        };
        timer.Start();
    }

    private void SetLiveOpen(bool open)
    {
        if (liveOpen == open) return;
        liveOpen = open && meetingElapsed != null;
        Render();
        Place();
    }

    public string Edge
    {
        get => edge;
        set { edge = Edges.All.Contains(value) ? value : Edges.Bottom; Render(); Place(); }
    }

    private bool Side => edge != Edges.Bottom;

    public void Display(OverlayPhase newPhase, string? newDetail = null)
    {
        bool changed = newPhase != phase;
        phase = newPhase;
        detail = newDetail;
        copied = false;
        if (phase != OverlayPhase.Hidden)
        {
            hovering = false;
            sideHint = null;
        }
        Render();
        Place(animated: changed);
    }

    private void SetHovering(bool value)
    {
        if (!value && sideHint != null)
        {
            sideHint = null;
            UpdateHint();
        }
        if (phase != OverlayPhase.Hidden || dragging || offer != null || hovering == value) return;
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
        OverlayPhase.Error => detail ?? "",
        _ => "",
    };

    private bool IsRecording => phase is OverlayPhase.Listening or OverlayPhase.HandsFree or OverlayPhase.Command;

    /// The bar's size for its state, or null when it should not be on screen.
    private Size? CurrentSize()
    {
        // Offers and a meeting being recorded always show, even with the idle bar off or snoozed.
        if (ShowsOffer) return expanded ? CardSize : CompactSize;
        if (ShowsMeetingPill) return hovering ? (Side ? MeetingControlsSide : MeetingControls) : (Side ? MeetingPillSide : MeetingPill);
        if (phase == OverlayPhase.Hidden)
        {
            bool snoozed = hiddenUntil is { } until && until > DateTime.Now;
            if (!showsIdleBar || snoozed) return null;
            if (hovering) return Side ? HoveredSide : Hovered;
            return Side ? IdleSide : Idle;
        }
        if (IsRecording) return Side ? RecordingSide : Recording;
        if (phase == OverlayPhase.Unplaced) return UnplacedSize;
        return Side ? BusySide : Busy;
    }

    // MARK: - Content

    private void Render()
    {
        meetingClocks.Clear();
        bool idleHandle = phase == OverlayPhase.Hidden && !hovering && !ShowsMeetingPill && !ShowsOffer;
        Root.Background = Frozen(PillColor, idleHandle ? 0.5 : 0.82);
        Root.BorderBrush = Frozen(WhiteColor, idleHandle ? 0.25 : 0.14);
        Root.ToolTip = null;
        foreach (var bar in bars)
        {
            (bar.Parent as Panel)?.Children.Remove(bar);
            bar.Fill = phase == OverlayPhase.Command ? Orange : Blue;
        }

        if (ShowsOffer && offer is { } current)
        {
            Root.Child = expanded ? CardContent(current) : CompactContent(current);
        }
        else if (ShowsMeetingPill)
        {
            Root.Child = hovering ? (Side ? MeetingControlsSideContent() : MeetingControlsContent())
                                  : (Side ? MeetingSideContent() : MeetingContent());
        }
        else if (phase == OverlayPhase.Hidden)
        {
            Root.Child = hovering ? (Side ? IdleSideContent() : IdleContent()) : null;
        }
        else if (IsRecording)
        {
            Root.Child = Side ? RecordingSideContent() : RecordingContent();
        }
        else if (phase == OverlayPhase.Unplaced)
        {
            Root.Child = UnplacedContent();
        }
        else
        {
            Root.Child = Side ? StatusIcon() : BusyContent();
            if (Side) Root.ToolTip = Label;
        }
    }

    private UIElement MeetingContent()
    {
        var row = new DockPanel { Margin = new Thickness(11, 0, 3, 0), LastChildFill = false };
        var stop = ActionButton("\uE71A", Accent, "Parar gravação da reunião", () => OnToggleMeeting(), 22);
        DockPanel.SetDock(stop, Dock.Right);
        row.Children.Add(stop);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(RecordingDot());
        var clock = Clock(time => Meeting.Clock(time.TotalSeconds), 12);
        clock.Margin = new Thickness(7, 0, 0, 0);
        left.Children.Add(clock);
        row.Children.Add(left);
        return row;
    }

    private UIElement MeetingSideContent()
    {
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var dot = RecordingDot();
        dot.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(dot);
        var minutes = Clock(time => ((int)time.TotalMinutes).ToString("00"), 11);
        minutes.Margin = new Thickness(0, 5, 0, 0);
        column.Children.Add(minutes);
        column.Children.Add(Clock(time => time.Seconds.ToString("00"), 11));
        var stop = ActionButton("\uE71A", Accent, "Parar gravação da reunião", () => OnToggleMeeting(), 22);
        stop.Margin = new Thickness(0, 5, 0, 0);
        column.Children.Add(stop);
        return column;
    }

    /// While a meeting is recorded, under the mouse: ● 12:34   [🎤] [≡] [■].
    private UIElement MeetingControlsContent()
    {
        var row = new DockPanel { Margin = new Thickness(12, 0, 5, 0), LastChildFill = false };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in MeetingButtons())
        {
            button.Margin = new Thickness(4, 0, 0, 0);
            buttons.Children.Add(button);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(RecordingDot());
        var clock = Clock(time => Meeting.Clock(time.TotalSeconds), 12);
        clock.Margin = new Thickness(7, 0, 0, 0);
        left.Children.Add(clock);
        row.Children.Add(left);
        return row;
    }

    private UIElement MeetingControlsSideContent()
    {
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var dot = RecordingDot();
        dot.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(dot);
        var minutes = Clock(time => ((int)time.TotalMinutes).ToString("00"), 11);
        minutes.Margin = new Thickness(0, 5, 0, 0);
        column.Children.Add(minutes);
        column.Children.Add(Clock(time => time.Seconds.ToString("00"), 11));
        bool first = true;
        foreach (var button in MeetingButtons())
        {
            button.Margin = new Thickness(0, first ? 8 : 4, 0, 0);
            first = false;
            column.Children.Add(button);
        }
        return column;
    }

    /// Dictate (hands-free), show or hide the live transcript, stop and summarize.
    private IEnumerable<Border> MeetingButtons()
    {
        yield return ActionButton("\uE720", Frozen(WhiteColor, 0.12), "Ditar sem segurar a tecla", () => OnDictate());
        yield return ActionButton("\uE8BD", Frozen(WhiteColor, liveOpen ? 0.3 : 0.12),
            liveOpen ? "Esconder a transcrição ao vivo" : "Mostrar a transcrição ao vivo", () => SetLiveOpen(!liveOpen));
        yield return ActionButton("\uE71A", Accent, "Parar e resumir a reunião", () => OnToggleMeeting());
    }

    /// The notice as it arrives: icon, title and a short subtitle.
    private UIElement CompactContent(Card current)
    {
        var row = new DockPanel { Margin = new Thickness(13, 0, 13, 0), LastChildFill = false };
        var when = Subtitle(current.CompactSubtitle ?? "", current.Dot);
        when.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(when, Dock.Right);
        row.Children.Add(when);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Glyph(current.Glyph, Accent, 14));
        var title = Text(current.Title, 13);
        title.Margin = new Thickness(9, 0, 0, 0);
        left.Children.Add(title);
        row.Children.Add(left);
        return row;
    }

    /// The 380×64 card: icon in an accent square, title and subtitle, its buttons on the right.
    private UIElement CardContent(Card current)
    {
        var row = new DockPanel { Margin = new Thickness(13, 0, 13, 0), LastChildFill = true };
        var icon = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(6), Background = Frozen(AccentColor, 0.2),
            VerticalAlignment = VerticalAlignment.Center, Child = Glyph(current.Glyph, Accent, 16),
        };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        if (current.Dismissable)
        {
            var dismiss = ActionButton("\uE711", Frozen(WhiteColor, 0.12), "Dispensar", () => DismissOffer(current));
            dismiss.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(dismiss, Dock.Right);
            row.Children.Add(dismiss);
        }
        var run = PrimaryButton(current.ActionLabel, current.ActionShowsSymbol, () => RunOffer(current));
        run.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(run, Dock.Right);
        row.Children.Add(run);
        if (current.SecondaryLabel is { } label)
        {
            var secondary = QuietButton(label, () => RunSecondary(current));
            DockPanel.SetDock(secondary, Dock.Right);
            row.Children.Add(secondary);
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 4, 0) };
        texts.Children.Add(Text(current.Title, 14));
        var subtitle = Subtitle(current.Subtitle, current.Dot);
        subtitle.Margin = new Thickness(0, 2, 0, 0);
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.MaxHeight = 32;
        subtitle.ToolTip = current.Subtitle;
        texts.Children.Add(subtitle);
        row.Children.Add(texts);
        return row;
    }

    /// 12 pt in White at 0.6, after a dot when there is one ("● Google Meet · agora").
    private static TextBlock Subtitle(string text, Color? dot)
    {
        var block = new TextBlock
        {
            FontSize = 12, Foreground = Frozen(WhiteColor, 0.6), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (dot is { } color) block.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = Frozen(color) });
        block.Inlines.Add(new System.Windows.Documents.Run(text));
        return block;
    }

    private void RunSecondary(Card current)
    {
        ClearOffer();
        current.Secondary?.Invoke();
    }

    private void RunOffer(Card current)
    {
        ClearOffer();
        current.Run();
    }

    private void DismissOffer(Card current)
    {
        ClearOffer();
        current.Dismissed();
    }

    private static Ellipse RecordingDot() => new()
    {
        Width = 8, Height = 8, Fill = Accent, VerticalAlignment = VerticalAlignment.Center,
    };

    /// A mono time readout that MeetingElapsed updates in place.
    private TextBlock Clock(Func<TimeSpan, string> format, double size)
    {
        var block = new TextBlock
        {
            Text = format(meetingElapsed ?? TimeSpan.Zero), FontSize = size, FontWeight = FontWeights.Medium, Foreground = White,
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        meetingClocks.Add((block, format));
        return block;
    }

    private UIElement IdleContent()
    {
        var row = new DockPanel { Margin = new Thickness(12, 0, 5, 0), LastChildFill = false };
        var record = RecordMeetingButton();
        record.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(record, Dock.Right);
        row.Children.Add(record);
        var mic = ActionButton("", Accent, "Ditar sem segurar a tecla", () => OnDictate());
        DockPanel.SetDock(mic, Dock.Right);
        row.Children.Add(mic);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Text("Ditar", 13));
        if (dictateKeys.Count > 0) left.Children.Add(KeyCaps(dictateKeys, new Thickness(8, 0, 0, 0)));
        row.Children.Add(left);
        return row;
    }

    /// On a side, just the two buttons; the one under the pointer is named in the hint beside the bar (HintWindow).
    private UIElement IdleSideContent()
    {
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        column.Children.Add(Hinted(ActionButton("", Accent, "Ditar sem segurar a tecla", () => OnDictate()), SideHint.Dictate));
        var record = Hinted(RecordMeetingButton(), SideHint.Meeting);
        record.Margin = new Thickness(0, 6, 0, 0);
        column.Children.Add(record);
        return column;
    }

    /// The hint names the button in place of its system tooltip, while the pointer is on it.
    private Border Hinted(Border button, SideHint which)
    {
        button.ToolTip = null;
        button.MouseEnter += (_, _) => SetSideHint(which);
        button.MouseLeave += (_, _) => { if (sideHint == which) SetSideHint(null); };
        return button;
    }

    private void SetSideHint(SideHint? value)
    {
        if (sideHint == value) return;
        sideHint = value;
        UpdateHint();
    }

    /// ◎: starts recording a meeting from the open bar.
    private Border RecordMeetingButton() =>
        ActionButton("\uE7C8", Frozen(WhiteColor, 0.12), "Gravar reunião", () => OnToggleMeeting());

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

    /// A dictation that found no text box: what was said, to copy, or paste later with the shortcut.
    private UIElement UnplacedContent()
    {
        var text = detail ?? "";
        var column = new DockPanel { Margin = new Thickness(14, 12, 14, 12), LastChildFill = true };

        var header = new DockPanel { LastChildFill = true };
        var close = ActionButton("\uE711", Brushes.Transparent, "Fechar", () => OnDismissUnplaced(), 20);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var cursor = Glyph("\uE77F", Accent, 12);
        DockPanel.SetDock(cursor, Dock.Left);
        header.Children.Add(cursor);
        var title = Text("Nenhum campo de texto para colar", 13);
        title.Margin = new Thickness(8, 0, 0, 0);
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);
        column.Children.Add(header);

        var footer = new DockPanel { LastChildFill = false };
        var copy = PrimaryButton(copied ? "\u2713 Copiado" : "Copiar", false, CopyUnplaced);
        copy.Height = 28;
        DockPanel.SetDock(copy, Dock.Right);
        footer.Children.Add(copy);
        if (pasteKeys.Count > 0)
        {
            var hint = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            hint.Children.Add(new TextBlock
            {
                Text = "Ou cole depois com", FontSize = 11, Foreground = Frozen(WhiteColor, 0.5), VerticalAlignment = VerticalAlignment.Center,
            });
            hint.Children.Add(KeyCaps(pasteKeys, new Thickness(6, 0, 0, 0)));
            footer.Children.Add(hint);
        }
        DockPanel.SetDock(footer, Dock.Bottom);
        column.Children.Add(footer);

        column.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, Foreground = Frozen(WhiteColor, 0.85), TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, Margin = new Thickness(0, 8, 0, 8),
            VerticalAlignment = VerticalAlignment.Top, ToolTip = text,
        });
        return column;
    }

    private void CopyUnplaced()
    {
        OnCopyUnplaced();
        copied = true;
        Render();
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

    internal static UIElement KeyCaps(IReadOnlyList<string> keys, Thickness margin)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in keys)
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

    /// The card's main button: white with dark text, 34 tall, 8 pt corners, optionally with the Lidiflow symbol.
    private Border PrimaryButton(string text, bool withSymbol, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (withSymbol && Symbol() is { } image)
        {
            content.Children.Add(new Image { Source = image, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        }
        content.Children.Add(new TextBlock
        {
            Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Frozen(PillColor),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var button = new Border
        {
            Height = 34, CornerRadius = new CornerRadius(8), Background = White, Cursor = Cursors.Hand,
            Padding = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Child = content,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        Clickable(button, action);
        return button;
    }

    /// A text-only button (Descartar).
    private Border QuietButton(string text, Action action)
    {
        var button = new Border
        {
            Height = 34, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Padding = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Frozen(WhiteColor, 0.75),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        Clickable(button, action);
        return button;
    }

    private static BitmapImage? Symbol()
    {
        if (symbol != null) return symbol;
        try
        {
            symbol = new BitmapImage();
            symbol.BeginInit();
            symbol.UriSource = new Uri("pack://application:,,,/Assets/AppIcon.png");
            symbol.DecodePixelWidth = 48;
            symbol.EndInit();
            symbol.Freeze();
        }
        catch (Exception e)
        {
            Log.Write("Bar symbol: " + e.Message);
            symbol = null;
        }
        return symbol;
    }

    /// Clicks on release, unless the press turned into a drag of the bar.
    private void Clickable(Border button, Action action)
    {
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
    }

    /// A 26×26 button with 6 pt corners (concentric with the bar's 10). A press that turns into a drag never clicks.
    private Border ActionButton(string glyph, Brush fill, string help, Action action, double side = 26)
    {
        var button = new Border
        {
            Width = side, Height = side, CornerRadius = new CornerRadius(6), Background = fill, Cursor = Cursors.Hand,
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
        menu.Items.Add(Item(IsMeetingRecording() ? "Parar gravação da reunião" : "Gravar reunião", () => OnToggleMeeting()));
        menu.Items.Add(new Separator());
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

    /// Re-anchors the bar on the monitor under the mouse. `animated` (a new phase) grows or shrinks it to its new size.
    public void Place(bool animated = false)
    {
        if (dragging) return;
        animated &= Animates;
        if (CurrentSize() is not { } size)
        {
            livePanel.Hide();
            hint.Hide();
            if (!animated || !IsVisible || fadingOut)
            {
                if (!fadingOut) Hide();
                return;
            }
            resizeTimer?.Stop();
            fadingOut = true;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fade.Completed += (_, _) =>
            {
                if (!fadingOut) return;
                fadingOut = false;
                if (CurrentSize() == null) Hide();
                Root.BeginAnimation(OpacityProperty, null);
                Root.Opacity = 1;
            };
            Root.BeginAnimation(OpacityProperty, fade);
            return;
        }
        if (fadingOut)
        {
            fadingOut = false;
            Root.BeginAnimation(OpacityProperty, null);
            Root.Opacity = 1;
        }
        var area = MouseMonitor();
        currentMonitor = area.Monitor;
        var rect = Slot(edge, size, area);
        if (!animated)
        {
            // A grow still running would pull the bar back to its old target.
            resizeTimer?.Stop();
            if (!IsVisible) Show();
            SetBounds(rect, Native.SWP_SHOWWINDOW);
            FollowBar(rect, area);
            return;
        }
        GrowTo(rect, area);
    }

    private void SetBounds(Rect rect, uint extraFlags = 0) =>
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height,
            Native.SWP_NOACTIVATE | extraFlags);

    /// The live transcript and the hint follow the bar.
    private void FollowBar(Rect rect, MonitorArea area)
    {
        placedRect = rect;
        placedScale = area.Scale;
        if (liveOpen && meetingElapsed != null) livePanel.PlaceBeside(rect, edge, area.Scale);
        else livePanel.Hide();
        UpdateHint();
    }

    /// The bar grows out of its handle (or out of nothing, when the idle bar is off) like a spring settling, its new content
    /// fading in.
    private void GrowTo(Rect target, MonitorArea area)
    {
        Rect start;
        if (IsVisible)
        {
            Native.GetWindowRect(handle, out var window);
            start = new Rect(window.left, window.top, window.right - window.left, window.bottom - window.top);
        }
        else
        {
            start = Slot(edge, Side ? IdleSide : Idle, area);
            Root.Opacity = 1;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
            Show();
            SetBounds(start, Native.SWP_SHOWWINDOW);
        }
        livePanel.Hide();
        hint.Hide();
        if (Root.Child is FrameworkElement content)
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        var started = DateTime.Now;
        var duration = TimeSpan.FromMilliseconds(280);
        resizeTimer?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        resizeTimer = timer;
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.Now - started).TotalMilliseconds / duration.TotalMilliseconds);
            double k = t >= 1 ? 1 : Springy(t);
            SetBounds(new Rect(start.X + (target.X - start.X) * k, start.Y + (target.Y - start.Y) * k,
                               Math.Max(1, start.Width + (target.Width - start.Width) * k),
                               Math.Max(1, start.Height + (target.Height - start.Height) * k)));
            if (t < 1) return;
            timer.Stop();
            SetBounds(target);
            FollowBar(target, area);
        };
        timer.Start();
    }

    /// cubic-bezier(0.2, 1.1, 0.3, 1): an ease-out with a slight overshoot, like the Mac's.
    private static double Springy(double t)
    {
        double lo = 0, hi = 1, s = t;
        for (int i = 0; i < 24; i++)
        {
            s = (lo + hi) / 2;
            if (Bezier(s, 0.2, 0.3) < t) lo = s; else hi = s;
        }
        return Bezier(s, 1.1, 1);

        static double Bezier(double s, double p1, double p2) =>
            3 * (1 - s) * (1 - s) * s * p1 + 3 * (1 - s) * s * s * p2 + s * s * s;
    }

    /// On a side, the hovered button is named beside the bar: the microphone with its keys, or the meeting button.
    private void UpdateHint()
    {
        if (!ShowsHint || sideHint is not { } which)
        {
            hint.Hide();
            return;
        }
        // Centers of the two buttons: 6 px padding + half of 26, then 6 px spacing + another 26.
        if (which == SideHint.Dictate) hint.ShowBeside(placedRect, edge, placedScale, 19, "Ditar", dictateKeys);
        else hint.ShowBeside(placedRect, edge, placedScale, 51, "Gravar reunião", Array.Empty<string>());
    }

    /// The hovered idle bar on a side names its hovered button beside it.
    private bool ShowsHint => phase == OverlayPhase.Hidden && hovering && Side && !ShowsMeetingPill && !ShowsOffer && !dragging
                              && IsVisible && !fadingOut;

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
            sideHint = null;
            livePanel.Hide();
            hint.Hide();
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
