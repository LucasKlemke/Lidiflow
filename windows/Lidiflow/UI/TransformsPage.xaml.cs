using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lidiflow.Core;
using Lidiflow.Platform;

namespace Lidiflow.UI;

/// Saved rewrites applied to the selected text with a shortcut.
public partial class TransformsPage : UserControl
{
    private readonly AppModel model;
    private readonly ObservableCollection<Transform> suggestions = new();
    private bool loading;
    private bool recording;

    public TransformsPage(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        TransformList.ItemsSource = model.Transforms;
        AutoTransformBox.ItemsSource = model.Transforms;
        Suggestions.ItemsSource = suggestions;
        model.ShortcutsChanged += () => { UpdateIntro(); ShowSelected(); };
        Loaded += (_, _) => Load();
        Unloaded += (_, _) => StopRecording();
    }

    private Transform? Selected => TransformList.SelectedItem as Transform;

    private void Load()
    {
        loading = true;
        EnabledBox.IsChecked = model.TransformsEnabled;
        AutoBox.IsChecked = model.AutoTransformEnabled;
        AutoTransformBox.SelectedItem = model.Transforms.FirstOrDefault(t => t.Id == model.AutoTransformId)
                                        ?? model.Transforms.FirstOrDefault();
        loading = false;
        if (TransformList.SelectedIndex < 0 && model.Transforms.Count > 0) TransformList.SelectedIndex = 0;
        UpdateIntro();
        ShowSelected();
    }

    private void UpdateIntro()
    {
        var first = model.DefaultShortcut(0);
        Intro.Text = "Selecione um texto em qualquer app e aperte o atalho de um transform: a IA reescreve o texto no lugar."
            + (first != null
                ? $" Sem atalho próprio, os nove primeiros usam o atalho de ditar + a posição ({first.Label})."
                : "");
    }

    private void ShowSelected()
    {
        var transform = Selected;
        Editor.IsEnabled = transform != null;
        loading = true;
        NameBox.Text = transform?.Name ?? "";
        PromptBox.Text = transform?.Prompt ?? "";
        loading = false;

        var index = transform == null ? -1 : model.Transforms.IndexOf(transform);
        var shortcut = index >= 0 ? model.ShortcutFor(index) : null;
        ShortcutText.Text = shortcut == null
            ? "Sem atalho"
            : transform?.Shortcut == null ? $"{shortcut.Label} (padrão)" : shortcut.Label;
        ClearShortcutButton.Visibility = transform?.Shortcut != null ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.Content = recording ? "Pressione o atalho… (clique para cancelar)" : "Gravar atalho";
    }

    private void SetError(string? message)
    {
        ShortcutError.Text = message ?? "";
        ShortcutError.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopRecording();
        SetError(null);
        ShowSelected();
    }

    private void Enabled_Click(object sender, RoutedEventArgs e) => model.TransformsEnabled = EnabledBox.IsChecked == true;

    private void Auto_Click(object sender, RoutedEventArgs e) => model.AutoTransformEnabled = AutoBox.IsChecked == true;

    private void AutoTransform_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && AutoTransformBox.SelectedItem is Transform transform) model.AutoTransformId = transform.Id;
    }

    private void Name_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || Selected is not { } transform) return;
        transform.Name = NameBox.Text;
        model.SaveTransforms();
    }

    private void Prompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || Selected is not { } transform) return;
        transform.Prompt = PromptBox.Text;
        model.SaveTransforms();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var transform = new Transform { Name = "Novo transform", Prompt = "Descreva aqui o que fazer com o texto." };
        model.Transforms.Add(transform);
        model.SaveTransforms();
        TransformList.SelectedItem = transform;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } transform) return;
        var index = model.Transforms.IndexOf(transform);
        model.Transforms.Remove(transform);
        model.SaveTransforms();
        if (model.Transforms.Count > 0) TransformList.SelectedIndex = Math.Min(index, model.Transforms.Count - 1);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int offset)
    {
        if (Selected is not { } transform) return;
        var index = model.Transforms.IndexOf(transform);
        var target = index + offset;
        if (target < 0 || target >= model.Transforms.Count) return;
        model.Transforms.Move(index, target);
        model.SaveTransforms();
        TransformList.SelectedItem = transform;
        ShowSelected();
    }

    // MARK: - Shortcut

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (recording)
        {
            StopRecording();
            return;
        }
        if (Selected is not { } transform || model.RecordShortcut == null) return;
        SetError(null);
        recording = true;
        ShowSelected();
        model.RecordShortcut(combo =>
        {
            if (!recording) return;
            recording = false;
            if (combo != null)
            {
                var index = model.Transforms.IndexOf(transform);
                var conflict = model.Conflict(combo, new TransformSlot(index < 0 ? null : index));
                if (conflict != null)
                {
                    SetError(conflict);
                }
                else
                {
                    transform.Shortcut = combo;
                    model.SaveTransforms();
                }
            }
            ShowSelected();
        });
    }

    private void StopRecording()
    {
        if (!recording) return;
        recording = false;
        model.CancelShortcutRecording?.Invoke();
        ShowSelected();
    }

    private void ClearShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } transform) return;
        transform.Shortcut = null;
        model.SaveTransforms();
        SetError(null);
        ShowSelected();
    }

    // MARK: - Suggestions

    private async void Suggest_Click(object sender, RoutedEventArgs e)
    {
        var text = SampleBox.Text.Trim();
        if (text.Length == 0)
        {
            SuggestStatus.Text = "Cole um texto primeiro.";
            return;
        }
        SuggestButton.IsEnabled = false;
        SuggestStatus.Text = "Pensando…";
        try
        {
            var result = await Llm.SuggestTransformsAsync(text);
            suggestions.Clear();
            foreach (var transform in result) suggestions.Add(transform);
            SuggestStatus.Text = result.Count == 0 ? "Nenhuma sugestão." : "";
        }
        catch (Exception ex)
        {
            SuggestStatus.Text = ex is UserFacingException ? ex.Message : "Falha ao pedir sugestões: " + ex.Message;
        }
        finally
        {
            SuggestButton.IsEnabled = true;
        }
    }

    private void AddSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Transform suggestion) return;
        suggestions.Remove(suggestion);
        model.Transforms.Add(new Transform { Name = suggestion.Name, Prompt = suggestion.Prompt });
        model.SaveTransforms();
    }
}
