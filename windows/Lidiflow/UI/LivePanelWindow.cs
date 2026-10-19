using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lidiflow.Core;
using Lidiflow.Platform;

namespace Lidiflow.UI;

/// The live transcript of the meeting being recorded (docs/reunioes.md, "Painel ao vivo"): 340 wide and up to 260
/// tall, above the bar (bottom) or beside it (sides). Like the bar, it never takes focus.
internal sealed class LivePanelWindow : Window
{
    private const double PanelWidth = 340, MaxPanelHeight = 260, Gap = 8;
    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color Dark = Color.FromRgb(0x14, 0x14, 0x14);
    private static readonly Color Accent = Color.FromRgb(0x1F, 0x6B, 0xFF);

    private readonly IntPtr handle;
    private readonly Border root;
    private readonly TextBlock clock;
    private readonly StackPanel lines = new();
    private readonly ScrollViewer scroller;
    private (Rect Bar, string Edge, double Scale)? anchor;

    public Action OnClose { get; set; } = () => { };

    public LivePanelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;

        var mono = (FontFamily)Application.Current.FindResource("MonoFont");
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
        var close = CloseButton();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Brush(Accent), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock
        {
            Text = "AO VIVO", FontFamily = mono, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush(White),
            Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        clock = new TextBlock
        {
            Text = Meeting.Clock(0), FontFamily = mono, FontSize = 11, Foreground = Brush(White, 0.6),
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        title.Children.Add(clock);
        header.Children.Add(title);

        scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = lines, Focusable = false,
        };
        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(scroller);
        root = new Border
        {
            Width = PanelWidth, MaxHeight = MaxPanelHeight, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Background = Brush(Dark, 0.94), BorderBrush = Brush(White, 0.14), Padding = new Thickness(14, 10, 10, 12),
            SnapsToDevicePixels = true, Child = layout,
        };
        root.SetResourceReference(TextElement.FontFamilyProperty, "BodyFont");
        Content = root;
        Utterances = Array.Empty<LiveUtterance>();

        handle = new WindowInteropHelper(this).EnsureHandle();
        var style = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
        Native.SetWindowLong(handle, Native.GWL_EXSTYLE, style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
    }

    private static SolidColorBrush Brush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    public TimeSpan Elapsed
    {
        set => clock.Text = Meeting.Clock(value.TotalSeconds);
    }

    /// The utterances so far, the newest scrolled into view.
    public IReadOnlyList<LiveUtterance> Utterances
    {
        set
        {
            lines.Children.Clear();
            if (value.Count == 0)
            {
                lines.Children.Add(new TextBlock
                {
                    Text = "Ouvindo… as falas aparecem aqui assim que cada um terminar de falar.",
                    FontSize = 12, Foreground = Brush(White, 0.6), TextWrapping = TextWrapping.Wrap,
                });
            }
            foreach (var utterance in value)
            {
                bool me = utterance.Source != "them";
                var line = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), LineHeight = 18 };
                line.Inlines.Add(new Run(me ? "Você  " : "Eles  ")
                {
                    FontWeight = FontWeights.SemiBold, Foreground = Brush(me ? Accent : White),
                });
                line.Inlines.Add(new Run(utterance.Text) { Foreground = Brush(White, 0.9) });
                lines.Children.Add(line);
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(scroller.ScrollToEnd));
            if (IsVisible && anchor is { } place) PlaceBeside(place.Bar, place.Edge, place.Scale);
        }
    }

    /// `bar` is the bar's rectangle in physical pixels: the panel goes above it (bottom) or beside it (sides).
    public void PlaceBeside(Rect bar, string edge, double scale)
    {
        anchor = (bar, edge, scale);
        root.Measure(new Size(PanelWidth, MaxPanelHeight));
        double w = Math.Round(PanelWidth * scale), h = Math.Round(Math.Min(MaxPanelHeight, root.DesiredSize.Height) * scale);
        double gap = Math.Round(Gap * scale);
        double x, y;
        switch (edge)
        {
            case OverlayWindow.Edges.Left:
                x = bar.Right + gap;
                y = Math.Round(bar.Y + bar.Height / 2 - h / 2);
                break;
            case OverlayWindow.Edges.Right:
                x = bar.X - gap - w;
                y = Math.Round(bar.Y + bar.Height / 2 - h / 2);
                break;
            default:
                x = Math.Round(bar.X + bar.Width / 2 - w / 2);
                y = bar.Y - gap - h;
                break;
        }
        if (!IsVisible) Show();
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, (int)x, (int)y, (int)w, (int)h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private Border CloseButton()
    {
        var button = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = Brush(White, 0.12), Cursor = Cursors.Hand,
            ToolTip = "Fechar", VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9,
                Foreground = Brush(White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "Fechar a transcrição ao vivo");
        bool pressed = false;
        button.MouseLeftButtonDown += (_, e) => { pressed = true; e.Handled = true; };
        button.MouseLeave += (_, _) => pressed = false;
        button.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            e.Handled = true;
            OnClose();
        };
        return button;
    }
}
