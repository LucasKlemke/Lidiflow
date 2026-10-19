using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Lidiflow.Core;

namespace Lidiflow.UI;

public partial class HomePage : UserControl
{
    private readonly AppModel model;
    private readonly ICollectionView history;

    public HomePage(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        DataContext = model;
        history = CollectionViewSource.GetDefaultView(model.History);
        HistoryList.ItemsSource = history;
        model.History.CollectionChanged += (_, _) => UpdateEmptyText();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppModel.DictateLabel)) UpdateEmptyText();
        };
        UpdateEmptyText();
    }

    private void UpdateEmptyText()
    {
        var query = Search.Text.Trim();
        if (model.History.Count == 0)
        {
            EmptyText.Text = $"Nenhum ditado ainda. Segure {model.DictateLabel} em qualquer app para começar.";
            EmptyText.Visibility = Visibility.Visible;
        }
        else if (query.Length > 0 && history.IsEmpty)
        {
            EmptyText.Text = $"Nada encontrado para “{query}”.";
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = Search.Text.Trim();
        if (query.Length == 0)
        {
            history.Filter = null;
        }
        else
        {
            history.Filter = item => item is Dictation d && d.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }
        UpdateEmptyText();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (model.History.Count == 0) return;
        var answer = MessageBox.Show(Window.GetWindow(this)!, "Apagar todo o histórico de ditados?", "Lidiflow",
                                     MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) model.ClearHistory();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not Dictation dictation) return;
        try { Clipboard.SetText(dictation.Text); } catch { return; }
        // A check in accent for a moment, and the buttons stay up meanwhile.
        var button = (Button)sender;
        var actions = (FrameworkElement)button.Parent;
        button.Content = "\uE73E";
        button.SetResourceReference(ForegroundProperty, "AccentFillColorDefaultBrush");
        var balloon = (ToolTip)button.ToolTip;
        balloon.Content = "Copiado";
        actions.Opacity = 1;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Content = "\uE8C8";
            button.ClearValue(ForegroundProperty);
            balloon.Content = "Copiar";
            actions.ClearValue(OpacityProperty);
        };
        timer.Start();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Dictation dictation) model.DeleteDictation(dictation);
    }
}
