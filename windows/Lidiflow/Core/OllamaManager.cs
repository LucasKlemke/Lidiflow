using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Lidiflow.Core;

public sealed record OllamaModel(string Id, string Name, string Detail, string Size);

/// Talks to a local Ollama: starts it, lists, pulls and deletes models.
public sealed class OllamaManager : ObservableObject, IModelOwner
{
    public static readonly OllamaModel[] Catalog =
    {
        new("qwen2.5:3b", "Qwen 2.5 3B", "Rápido e bom em português", "1,9 GB"),
        new("llama3.2:3b", "Llama 3.2 3B", "Leve, da Meta", "2,0 GB"),
        new("phi3:mini", "Phi-3 Mini", "Compacto, da Microsoft", "2,2 GB"),
        new("qwen2.5:7b", "Qwen 2.5 7B", "Melhor qualidade, mais lento", "4,7 GB"),
        new("llama3.1:8b", "Llama 3.1 8B", "Equilibrado, da Meta", "4,9 GB"),
        new("mistral:7b", "Mistral 7B", "Ótimo para reescrita", "4,1 GB"),
        new("gemma2:9b", "Gemma 2 9B", "O mais capaz da lista, do Google", "5,4 GB"),
    };

    private readonly Dictionary<string, CancellationTokenSource> pulls = new();
    private bool isRunning;
    private string? lastError;
    private List<string> installed = new();

    public OllamaManager()
    {
        Items = new ObservableCollection<ModelItem>(
            Catalog.Select(model => new ModelItem(this, model.Id, model.Name, model.Detail, model.Size)));
    }

    public ObservableCollection<ModelItem> Items { get; }

    /// Called when the user picks another installed model.
    public Action<string>? OnUse { get; set; }

    public bool IsRunning { get => isRunning; private set => SetField(ref isRunning, value); }
    public string? LastError { get => lastError; private set => SetField(ref lastError, value); }

    /// The settings store the OpenAI-compatible URL (…/v1); the native API lives at the root.
    private static string Host
    {
        get
        {
            var url = AppSettings.Current.OllamaBaseUrl.TrimEnd('/');
            return url.EndsWith("/v1") ? url[..^3] : url;
        }
    }

    private static string? Binary
    {
        get
        {
            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Programs", "Ollama", "ollama.exe"),
            };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim(), "ollama.exe"));
            }
            return candidates.FirstOrDefault(File.Exists);
        }
    }

    public bool IsInstalledOnPc => Binary != null;

    public async Task RefreshAsync()
    {
        try
        {
            using var deadline = Http.Deadline(1.5);
            using var response = await Http.Client.GetAsync(Host + "/api/version", deadline.Token);
            IsRunning = response.IsSuccessStatusCode;
        }
        catch
        {
            IsRunning = false;
        }
        try
        {
            installed = IsRunning ? await InstalledModelsAsync() : new List<string>();
        }
        catch
        {
            installed = new List<string>();
        }
        var selected = AppSettings.Current.AiProvider == LlmProvider.Ollama
            ? AppSettings.Current.Model(LlmProvider.Ollama)
            : null;
        foreach (var item in Items)
        {
            item.IsInstalled = IsInstalled(item.Id);
            item.IsSelected = item.Id == selected;
        }
        OnPropertyChanged(nameof(IsInstalledOnPc));
    }

    public bool IsInstalled(string id)
    {
        var full = id.Contains(':') ? id : id + ":latest";
        return installed.Contains(full) || installed.Contains(id);
    }

    /// Starts `ollama serve` in the background.
    public async Task StartAsync()
    {
        var binary = Binary;
        if (binary == null)
        {
            LastError = "O Ollama não está instalado (baixe em ollama.com)";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(binary, "serve") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch
        {
            LastError = "Não foi possível iniciar o Ollama";
            return;
        }
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(300);
            await RefreshAsync();
            if (IsRunning) return;
        }
    }

    public async void Download(ModelItem item)
    {
        if (pulls.ContainsKey(item.Id)) return;
        LastError = null;
        var cancel = new CancellationTokenSource();
        pulls[item.Id] = cancel;
        item.Progress = 0;
        try
        {
            var payload = new JsonObject { ["name"] = item.Id, ["stream"] = true };
            using var request = new HttpRequestMessage(HttpMethod.Post, Host + "/api/pull")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel.Token));
            // Each line is a JSON status update: {"status", "total", "completed"} or {"error"}.
            string? line;
            while ((line = await reader.ReadLineAsync(cancel.Token)) != null)
            {
                var update = JsonHelpers.Parse(line);
                var error = JsonHelpers.String(() => update?["error"]);
                if (error != null)
                {
                    LastError = "Ollama: " + error;
                    return;
                }
                double total = Number(update, "total"), completed = Number(update, "completed");
                if (total > 0) item.Progress = completed / total;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            LastError = "Download interrompido: " + e.Message;
        }
        finally
        {
            pulls.Remove(item.Id);
            item.Progress = null;
            await RefreshAsync();
        }
    }

    private static double Number(JsonNode? node, string key)
    {
        try
        {
            return node?[key] is JsonValue value && value.TryGetValue(out double number) ? number : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Cancel(ModelItem item)
    {
        if (pulls.TryGetValue(item.Id, out var cancel)) cancel.Cancel();
    }

    public void Use(ModelItem item) => OnUse?.Invoke(item.Id);

    public async void Delete(ModelItem item)
    {
        try
        {
            var payload = new JsonObject { ["name"] = item.Id };
            using var request = new HttpRequestMessage(HttpMethod.Delete, Host + "/api/delete")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var deadline = Http.Deadline(10);
            using var _ = await Http.Client.SendAsync(request, deadline.Token);
        }
        catch { }
        await RefreshAsync();
    }

    public static async Task<List<string>> InstalledModelsAsync()
    {
        using var deadline = Http.Deadline(3);
        var body = await Http.Client.GetStringAsync(Host + "/api/tags", deadline.Token);
        var models = JsonHelpers.Parse(body)?["models"] as JsonArray ?? new JsonArray();
        return models
            .Select(model => JsonHelpers.String(() => model?["name"]))
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }
}
