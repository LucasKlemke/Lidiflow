using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Lidiflow.UI;

/// The app window: sidebar plus one page at a time. Closing it only hides it; the app stays in the tray.
public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> pages;

    public MainWindow(AppModel model)
    {
        InitializeComponent();
        DataContext = model;
        pages = new Dictionary<string, UserControl>
        {
            ["home"] = new HomePage(model),
            ["models"] = new ModelsPage(model),
            ["transforms"] = new TransformsPage(model),
            ["dictionary"] = new DictionaryPage(model),
            ["settings"] = new SettingsPage(model),
        };
        Nav.SelectedIndex = 0;
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListBoxItem { Tag: string tag } && pages.TryGetValue(tag, out var page))
        {
            PageHost.Content = page;
            Scroller.ScrollToTop();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
