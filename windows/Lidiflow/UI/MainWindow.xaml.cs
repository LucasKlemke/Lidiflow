using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Lidiflow.UI;

/// A page that scrolls its own parts (e.g. a pinned footer and a side pane) instead of inside the window's scroller.
public interface IOwnScrolling
{
    bool OwnsScrolling { get; }
    event Action? OwnsScrollingChanged;
}

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
            ["meetings"] = new MeetingsPage(model),
            ["models"] = new ModelsPage(model),
            ["transforms"] = new TransformsPage(model),
            ["dictionary"] = new DictionaryPage(model),
            ["settings"] = new SettingsPage(model),
        };
        model.ShowSettings = () => Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().First(i => (string)i.Tag == "settings");
        foreach (var page in pages.Values)
            if (page is IOwnScrolling owner) owner.OwnsScrollingChanged += () => ApplyScrolling(page);
        Nav.SelectedIndex = 0;
    }

    /// The window scroller and the page's margins, unless the page scrolls its own parts.
    private void ApplyScrolling(UserControl page)
    {
        if (PageHost.Content != page) return;
        bool own = page is IOwnScrolling { OwnsScrolling: true };
        Scroller.VerticalScrollBarVisibility = own ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        PageHost.Margin = own ? new Thickness(0) : new Thickness(32, 24, 32, 32);
        PageHost.MaxWidth = own ? double.PositiveInfinity : 900;
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
            ApplyScrolling(page);
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
