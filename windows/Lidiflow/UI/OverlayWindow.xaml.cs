using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Lidiflow.Core;
using Lidiflow.Platform;

namespace Lidiflow.UI;

public enum OverlayPhase { Hidden, Listening, HandsFree, Command, Transcribing, Polishing, Thinking, Transforming, Error }

/// Small floating pill at the bottom of the screen that never takes focus or clicks.
public partial class OverlayWindow : Window
{
    private static readonly double[] Weights = { 0.5, 0.8, 1, 0.8, 0.5 };
    private static readonly Brush Blue = Frozen(Color.FromRgb(0x4D, 0x94, 0xFF));
    private static readonly Brush Orange = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));

    private readonly Rectangle[] bars;

    public OverlayWindow()
    {
        InitializeComponent();
        bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4 };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
            Native.SetWindowLong(handle, Native.GWL_EXSTYLE,
                style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
        };
        SizeChanged += (_, _) => Reposition();
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// Microphone RMS level; drives the bars.
    public float Level
    {
        set
        {
            var level = Math.Min(value * 12, 1);
            for (int i = 0; i < bars.Length; i++) bars[i].Height = 4 + 18 * level * Weights[i];
        }
    }

    public void Display(OverlayPhase phase, string? detail = null)
    {
        if (phase == OverlayPhase.Hidden)
        {
            Hide();
            return;
        }

        bool listening = phase is OverlayPhase.Listening or OverlayPhase.HandsFree or OverlayPhase.Command;
        Bars.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        WarningIcon.Visibility = phase == OverlayPhase.Error ? Visibility.Visible : Visibility.Collapsed;
        Spinner.Visibility = !listening && phase != OverlayPhase.Error ? Visibility.Visible : Visibility.Collapsed;
        foreach (var bar in bars) bar.Fill = phase == OverlayPhase.Command ? Orange : Blue;
        Caption.Text = Label(phase, detail);

        if (!IsVisible) Show();
        Reposition();
    }

    private static string Label(OverlayPhase phase, string? detail) => phase switch
    {
        OverlayPhase.Listening => "Ouvindo…",
        OverlayPhase.HandsFree => HandsFreeLabel(),
        OverlayPhase.Command => "Diga o que fazer…",
        OverlayPhase.Thinking => "Pensando…",
        OverlayPhase.Transcribing => "Transcrevendo…",
        OverlayPhase.Polishing => "Ajustando texto…",
        OverlayPhase.Transforming => $"✨ {detail}…",
        OverlayPhase.Error => detail ?? "Erro",
        _ => "",
    };

    private static string HandsFreeLabel()
    {
        var shortcuts = AppSettings.Current.Shortcuts;
        var combo = (shortcuts.TryGetValue(ShortcutAction.HandsFree, out var handsFree) ? handsFree.FirstOrDefault() : null)
            ?? (shortcuts.TryGetValue(ShortcutAction.Dictate, out var dictate) ? dictate.FirstOrDefault() : null);
        return combo == null ? "Mãos livres" : $"Mãos livres · {combo.Label} para parar";
    }

    /// Bottom center of the primary screen's work area.
    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 12;
    }
}
