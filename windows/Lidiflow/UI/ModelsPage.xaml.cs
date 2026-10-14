using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lidiflow.Core;

namespace Lidiflow.UI;

/// Where transcription and the AI run: this PC or the user's own API account.
public partial class ModelsPage : UserControl
{
    private readonly AppModel model;
    /// Set while the page fills its own controls, so their change events don't write back.
    private bool loading;

    public ModelsPage(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        WhisperModels.ItemsSource = model.Models.Items;
        OllamaModels.ItemsSource = model.Ollama.Items;
        TranscriptionProviderBox.ItemsSource = CloudTranscriber.Providers.Select(p => new ProviderItem(p)).ToList();
        AiProviderBox.ItemsSource = LlmProviders.Cloud.Select(p => new ProviderItem(p)).ToList();
        model.Models.PropertyChanged += (_, _) => UpdateWhisper();
        model.Ollama.PropertyChanged += (_, _) => UpdateOllama();
        Loaded += async (_, _) =>
        {
            Load();
            model.Models.Refresh();
            await model.Ollama.RefreshAsync();
        };
    }

    private LlmProvider CloudAiProvider =>
        AiProviderBox.SelectedItem is ProviderItem item ? item.Provider : AppSettings.Current.LastCloudProvider;

    private void Load()
    {
        loading = true;
        TranscribeLocal.IsChecked = model.Engine == TranscriptionEngine.Local;
        TranscribeCloud.IsChecked = model.Engine == TranscriptionEngine.Cloud;
        Select(TranscriptionProviderBox, model.TranscriptionProvider);
        Fill(TranscriptionModelBox, CloudTranscriber.Models(model.TranscriptionProvider), model.TranscriptionModel);
        TranscriptionKey.Password = "";
        TranscriptionKeyStatus.Text = KeyStatus(model.TranscriptionProvider);

        AiLocal.IsChecked = model.AiRunsLocally;
        AiCloud.IsChecked = !model.AiRunsLocally;
        var cloud = model.AiRunsLocally ? AppSettings.Current.LastCloudProvider : model.AiProvider;
        Select(AiProviderBox, cloud);
        Fill(AiModelBox, cloud.SuggestedModels(), AppSettings.Current.Model(cloud));
        AiKey.Password = "";
        AiKeyStatus.Text = KeyStatus(cloud);
        OllamaUrl.Text = model.OllamaBaseUrl;
        loading = false;

        UpdateSections();
        UpdateWhisper();
        UpdateOllama();
    }

    private static void Select(ComboBox box, LlmProvider provider) =>
        box.SelectedItem = box.Items.OfType<ProviderItem>().FirstOrDefault(item => item.Provider == provider);

    /// Fills an editable model box with suggestions, keeping the current choice even if it isn't one.
    private void Fill(ComboBox box, IEnumerable<string> suggestions, string current)
    {
        var wasLoading = loading;
        loading = true;
        var items = suggestions.ToList();
        if (current.Length > 0 && !items.Contains(current)) items.Insert(0, current);
        box.ItemsSource = items;
        box.Text = current;
        loading = wasLoading;
    }

    private string KeyStatus(LlmProvider provider) => model.HasKey(provider) ? "✓ Chave salva" : "Nenhuma chave salva";

    private void UpdateSections()
    {
        bool local = model.Engine == TranscriptionEngine.Local;
        LocalTranscription.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        CloudTranscription.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        LocalAi.Visibility = model.AiRunsLocally ? Visibility.Visible : Visibility.Collapsed;
        CloudAi.Visibility = model.AiRunsLocally ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateWhisper()
    {
        var manager = model.Models;
        bool installing = manager.WhisperProgress != null;
        WhisperStatus.Text = manager.WhisperInstalled
            ? "Instalado. A transcrição roda neste PC."
            : "Necessário para transcrever no PC. O Lidiflow baixa a versão oficial do GitHub.";
        InstallWhisper.Content = installing ? "Instalando…" : manager.WhisperInstalled ? "Reinstalar" : "Instalar whisper.cpp";
        InstallWhisper.IsEnabled = !installing;
        WhisperProgress.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
        WhisperProgress.Value = manager.WhisperProgress ?? 0;
        ShowError(WhisperError, manager.LastError);
    }

    private void UpdateOllama()
    {
        var ollama = model.Ollama;
        OllamaStatus.Text = ollama.IsRunning ? "Rodando"
            : ollama.IsInstalledOnPc ? "Instalado, mas parado"
            : "Não instalado. Baixe o Ollama para Windows e volte aqui.";
        StartOllama.Visibility = ollama.IsInstalledOnPc && !ollama.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        DownloadOllama.Visibility = ollama.IsInstalledOnPc ? Visibility.Collapsed : Visibility.Visible;
        ShowError(OllamaError, ollama.LastError);
    }

    private static void ShowError(TextBlock block, string? message)
    {
        block.Text = message ?? "";
        block.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    // MARK: - Transcription

    private void Engine_Checked(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        model.Engine = TranscribeLocal.IsChecked == true ? TranscriptionEngine.Local : TranscriptionEngine.Cloud;
        UpdateSections();
    }

    private void TranscriptionProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || TranscriptionProviderBox.SelectedItem is not ProviderItem item) return;
        model.TranscriptionProvider = item.Provider;
        Fill(TranscriptionModelBox, CloudTranscriber.Models(item.Provider), model.TranscriptionModel);
        TranscriptionKeyStatus.Text = KeyStatus(item.Provider);
    }

    private void TranscriptionModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && e.AddedItems.Count > 0 && e.AddedItems[0] is string value) model.TranscriptionModel = value;
    }

    private void TranscriptionModel_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = TranscriptionModelBox.Text.Trim();
        if (!loading && value.Length > 0 && value != model.TranscriptionModel) model.TranscriptionModel = value;
    }

    private void SaveTranscriptionKey_Click(object sender, RoutedEventArgs e)
    {
        model.SaveKey(TranscriptionKey.Password, model.TranscriptionProvider);
        TranscriptionKey.Password = "";
        TranscriptionKeyStatus.Text = KeyStatus(model.TranscriptionProvider);
    }

    private async void InstallWhisper_Click(object sender, RoutedEventArgs e)
    {
        await model.Models.InstallWhisperAsync();
    }

    // MARK: - AI

    private void Ai_Checked(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        model.AiProvider = AiLocal.IsChecked == true ? LlmProvider.Ollama : CloudAiProvider;
        if (AiCloud.IsChecked == true) Fill(AiModelBox, model.AiProvider.SuggestedModels(), model.AiModel);
        UpdateSections();
        _ = model.Ollama.RefreshAsync();
    }

    private void AiProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || AiProviderBox.SelectedItem is not ProviderItem item) return;
        model.AiProvider = item.Provider;
        Fill(AiModelBox, item.Provider.SuggestedModels(), model.AiModel);
        AiKeyStatus.Text = KeyStatus(item.Provider);
        ShowError(AiError, null);
    }

    private void AiModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && e.AddedItems.Count > 0 && e.AddedItems[0] is string value) model.AiModel = value;
    }

    private void AiModel_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = AiModelBox.Text.Trim();
        if (!loading && value.Length > 0 && value != model.AiModel) model.AiModel = value;
    }

    private async void RefreshAiModels_Click(object sender, RoutedEventArgs e)
    {
        var provider = CloudAiProvider;
        try
        {
            var models = await Llm.AvailableModelsAsync(provider);
            Fill(AiModelBox, models, AppSettings.Current.Model(provider));
            ShowError(AiError, null);
        }
        catch (Exception ex)
        {
            ShowError(AiError, ex is UserFacingException ? ex.Message : "Não foi possível listar os modelos: " + ex.Message);
        }
    }

    private void SaveAiKey_Click(object sender, RoutedEventArgs e)
    {
        var provider = CloudAiProvider;
        model.SaveKey(AiKey.Password, provider);
        AiKey.Password = "";
        AiKeyStatus.Text = KeyStatus(provider);
    }

    private void GetKey_Click(object sender, RoutedEventArgs e)
    {
        if (CloudAiProvider.KeyUrl() is string url) Open(url);
    }

    private async void StartOllama_Click(object sender, RoutedEventArgs e) => await model.Ollama.StartAsync();

    private void DownloadOllama_Click(object sender, RoutedEventArgs e) => Open("https://ollama.com/download/windows");

    private async void RefreshOllama_Click(object sender, RoutedEventArgs e) => await model.Ollama.RefreshAsync();

    private async void SaveOllamaUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = OllamaUrl.Text.Trim();
        if (url.Length == 0) return;
        model.OllamaBaseUrl = url;
        await model.Ollama.RefreshAsync();
    }

    // MARK: - Model rows

    private static ModelItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ModelItem;

    private void ModelDownload_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } item) item.Owner.Download(item); }

    private void ModelCancel_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } item) item.Owner.Cancel(item); }

    private void ModelUse_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } item) item.Owner.Use(item); }

    private void ModelDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var answer = MessageBox.Show(Window.GetWindow(this)!, $"Apagar o modelo {item.Name}?", "Lidiflow",
                                     MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) item.Owner.Delete(item);
    }
}
