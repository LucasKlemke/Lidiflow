using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lidiflow.Core;

namespace Lidiflow.UI;

/// Custom words, corrections and snippets applied to every dictation.
public partial class DictionaryPage : UserControl
{
    private readonly AppModel model;

    public DictionaryPage(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        WordList.ItemsSource = model.Words;
        ReplacementList.ItemsSource = model.Replacements;
        SnippetList.ItemsSource = model.Snippets;
    }

    private void AddWord_Click(object sender, RoutedEventArgs e)
    {
        var word = WordBox.Text.Trim();
        if (word.Length == 0) return;
        if (!model.Words.Contains(word, StringComparer.OrdinalIgnoreCase)) model.Words.Add(word);
        model.SaveVocabulary();
        WordBox.Clear();
        WordBox.Focus();
    }

    private void WordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddWord_Click(sender, e);
    }

    private void RemoveWord_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string word) return;
        model.Words.Remove(word);
        model.SaveVocabulary();
    }

    private void AddReplacement_Click(object sender, RoutedEventArgs e)
    {
        var from = FromBox.Text.Trim();
        if (from.Length == 0) return;
        model.Replacements.Add(new Replacement { From = from, To = ToBox.Text.Trim() });
        model.SaveVocabulary();
        FromBox.Clear();
        ToBox.Clear();
        FromBox.Focus();
    }

    private void RemoveReplacement_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Replacement replacement) return;
        model.Replacements.Remove(replacement);
        model.SaveVocabulary();
    }

    private void AddSnippet_Click(object sender, RoutedEventArgs e)
    {
        var trigger = TriggerBox.Text.Trim();
        var expansion = ExpansionBox.Text;
        if (trigger.Length == 0 || expansion.Trim().Length == 0) return;
        model.Snippets.Add(new Snippet { Trigger = trigger, Expansion = expansion });
        model.SaveVocabulary();
        TriggerBox.Clear();
        ExpansionBox.Clear();
        TriggerBox.Focus();
    }

    private void RemoveSnippet_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Snippet snippet) return;
        model.Snippets.Remove(snippet);
        model.SaveVocabulary();
    }
}
