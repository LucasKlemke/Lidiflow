using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lidiflow.Core;
using Lidiflow.Platform;

namespace Lidiflow.UI;

public partial class SettingsPage : UserControl
{
    private readonly AppModel model;
    private bool loading;
    /// The action whose new shortcut is being recorded, if any.
    private ShortcutAction? recordingFor;
    private (ShortcutAction Action, string Message)? error;

    public SettingsPage(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        DataPath.Text = Paths.Support;
        model.ShortcutsChanged += BuildShortcuts;
        Loaded += (_, _) => Load();
        Unloaded += (_, _) => StopRecording();
    }

    private void Load()
    {
        loading = true;
        Select(ThemeBox, model.Theme.ToString());
        LoadMicrophones();
        Select(LanguageBox, model.Language);
        SoundsBox.IsChecked = model.SoundsEnabled;
        PolishBox.IsChecked = model.PolishEnabled;
        StartupBox.IsChecked = model.LaunchAtStartup;
        loading = false;
        BuildShortcuts();
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == tag);

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && Enum.TryParse<Appearance>(TagOf(ThemeBox), out var theme)) model.Theme = theme;
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && TagOf(LanguageBox) is string language) model.Language = language;
    }

    private void LoadMicrophones()
    {
        MicrophoneBox.Items.Clear();
        MicrophoneBox.Items.Add(new ComboBoxItem { Content = "Automático", Tag = Microphones.Automatic });
        var devices = Microphones.Inputs();
        foreach (var device in devices) MicrophoneBox.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
        // A chosen microphone that is disconnected stays selectable, so the setting is not lost.
        if (model.Microphone != Microphones.Automatic && devices.All(d => d.Id != model.Microphone))
            MicrophoneBox.Items.Add(new ComboBoxItem { Content = "Microfone desconectado", Tag = model.Microphone });
        Select(MicrophoneBox, model.Microphone);
    }

    private void Microphone_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && TagOf(MicrophoneBox) is string microphone) model.Microphone = microphone;
    }

    private void Sounds_Click(object sender, RoutedEventArgs e) => model.SoundsEnabled = SoundsBox.IsChecked == true;

    private void Polish_Click(object sender, RoutedEventArgs e) => model.PolishEnabled = PolishBox.IsChecked == true;

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            model.LaunchAtStartup = StartupBox.IsChecked == true;
        }
        catch (Exception ex)
        {
            Log.Write("Startup: " + ex.Message);
            StartupBox.IsChecked = model.LaunchAtStartup;
        }
    }

    private void Microphone_Click(object sender, RoutedEventArgs e) => Open("ms-settings:privacy-microphone");

    private void OpenData_Click(object sender, RoutedEventArgs e) => Open(Paths.Support);

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    // MARK: - Shortcuts

    /// One card per action: its shortcuts as chips, plus a button to record another.
    private void BuildShortcuts()
    {
        ShortcutList.Children.Clear();
        foreach (var action in ShortcutActions.All)
        {
            var combos = model.CombosFor(action);
            var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var combo in combos)
            {
                var remove = new Button { Content = "✕", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0), FontSize = 10, ToolTip = "Remover" };
                var removed = combo;
                remove.Click += (_, _) => model.SetShortcuts(action, combos.Where(c => !c.Equals(removed)).ToList());
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock { Text = combo.Label, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(remove);
                chips.Children.Add(new Border { Style = (Style)FindResource("Chip"), Child = content });
            }
            if (combos.Count < ShortcutActions.MaxShortcuts)
            {
                var add = new Button
                {
                    Content = recordingFor == action ? "Pressione o atalho… (clique para cancelar)" : "+ Adicionar",
                    Margin = new Thickness(0, 0, 8, 8),
                };
                add.Click += (_, _) => ToggleRecording(action);
                chips.Children.Add(add);
            }

            var card = new StackPanel();
            card.Children.Add(new TextBlock { Text = action.Title(), FontWeight = FontWeights.SemiBold });
            card.Children.Add(new TextBlock { Text = action.Detail(), Style = (Style)FindResource("Subtle"), FontSize = 12 });
            card.Children.Add(chips);
            if (error is { } shown && shown.Action == action)
            {
                card.Children.Add(new TextBlock { Text = shown.Message, Style = (Style)FindResource("ErrorText") });
            }
            ShortcutList.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = card });
        }
    }

    private void ToggleRecording(ShortcutAction action)
    {
        if (recordingFor == action)
        {
            StopRecording();
            return;
        }
        StopRecording();
        if (model.RecordShortcut == null) return;
        error = null;
        recordingFor = action;
        BuildShortcuts();
        model.RecordShortcut(combo =>
        {
            if (recordingFor != action) return;
            recordingFor = null;
            if (combo != null)
            {
                var conflict = model.Conflict(combo, new ActionSlot(action));
                if (conflict != null)
                {
                    error = (action, conflict);
                }
                else
                {
                    model.SetShortcuts(action, model.CombosFor(action).Append(combo).ToList());
                    return; // SetShortcuts rebuilds the list.
                }
            }
            BuildShortcuts();
        });
    }

    private void StopRecording()
    {
        if (recordingFor == null) return;
        recordingFor = null;
        model.CancelShortcutRecording?.Invoke();
        BuildShortcuts();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        error = null;
        model.ResetShortcuts();
    }
}
