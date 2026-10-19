using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lidiflow.Platform;

namespace Lidiflow.UI;

/// "Ditar Shift dir. →" or "Gravar reunião →" beside the side bar's hovered button, pointing at it. It never takes focus or
/// the mouse (WS_EX_TRANSPARENT), so the bar under the cursor keeps its hover.
internal sealed class HintWindow : Window
{
    private const double Gap = 8, HintHeight = 34;
    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color Dark = Color.FromRgb(0x14, 0x14, 0x14);

    private readonly IntPtr handle;
    private readonly Border root;

    public HintWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;

        root = new Border
        {
            Height = HintHeight, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Background = Brush(Dark, 0.94), BorderBrush = Brush(White, 0.14), Padding = new Thickness(12, 0, 12, 0),
            SnapsToDevicePixels = true,
        };
        root.SetResourceReference(TextElement.FontFamilyProperty, "BodyFont");
        Content = root;

        handle = new WindowInteropHelper(this).EnsureHandle();
        var style = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
        Native.SetWindowLong(handle, Native.GWL_EXSTYLE,
            style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
    }

    private static SolidColorBrush Brush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    /// `bar` is the bar's rectangle in physical pixels; the hint goes on its inner side, the arrow toward it, level with the
    /// button whose center is `centerFromTop` (in DIPs) below the bar's top.
    public void ShowBeside(Rect bar, string edge, double scale, double centerFromTop, string title, IReadOnlyList<string> keys)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (edge == OverlayWindow.Edges.Left) row.Children.Add(Arrow("←", new Thickness(0, 0, 8, 0)));
        row.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Brush(White),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (keys.Count > 0) row.Children.Add(OverlayWindow.KeyCaps(keys, new Thickness(8, 0, 0, 0)));
        if (edge == OverlayWindow.Edges.Right) row.Children.Add(Arrow("→", new Thickness(keys.Count > 0 ? 5 : 8, 0, 0, 0)));
        root.Child = row;

        root.Measure(new Size(double.PositiveInfinity, HintHeight));
        double w = Math.Ceiling(root.DesiredSize.Width * scale), h = Math.Round(HintHeight * scale), gap = Math.Round(Gap * scale);
        double x = edge == OverlayWindow.Edges.Right ? bar.X - gap - w : bar.Right + gap;
        double y = Math.Round(bar.Y + centerFromTop * scale - h / 2);
        bool appearing = !IsVisible;
        if (appearing)
        {
            root.Opacity = 0;
            Show();
        }
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, (int)x, (int)y, (int)w, (int)h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        if (appearing) root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.12)));
    }

    private static TextBlock Arrow(string arrow, Thickness margin) => new()
    {
        Text = arrow, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush(White, 0.6), Margin = margin,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
