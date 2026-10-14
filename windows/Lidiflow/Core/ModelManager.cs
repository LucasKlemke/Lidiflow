using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Lidiflow.Core;

/// What a model row's buttons do, implemented by ModelManager (Whisper) and OllamaManager.
public interface IModelOwner
{
    void Download(ModelItem item);
    void Cancel(ModelItem item);
    void Use(ModelItem item);
    void Delete(ModelItem item);
}

/// One downloadable model, as shown in the Models page.
public sealed class ModelItem : ObservableObject
{
    private bool isInstalled;
    private bool isSelected;
    private double? progress;

    public ModelItem(IModelOwner owner, string id, string name, string detail, string size)
    {
        Owner = owner;
        Id = id;
        Name = name;
        Detail = detail;
        Size = size;
    }

    public IModelOwner Owner { get; }
    public string Id { get; }
    public string Name { get; }
    public string Detail { get; }
    public string Size { get; }

    public bool IsInstalled { get => isInstalled; set { if (SetField(ref isInstalled, value)) Refresh(); } }
    public bool IsSelected { get => isSelected; set { if (SetField(ref isSelected, value)) Refresh(); } }
    /// Download progress 0…1 while downloading, null otherwise.
    public double? Progress { get => progress; set { if (SetField(ref progress, value)) Refresh(); } }

    public double ProgressValue => progress ?? 0;
    public Visibility ProgressVisibility => Show(progress != null);
    public Visibility DownloadVisibility => Show(!isInstalled && progress == null);
    public Visibility CancelVisibility => Show(progress != null);
    public Visibility UseVisibility => Show(isInstalled && !isSelected);
    public Visibility InUseVisibility => Show(isInstalled && isSelected);
    public Visibility DeleteVisibility => Show(isInstalled);

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void Refresh()
    {
        foreach (var name in new[] { nameof(ProgressValue), nameof(ProgressVisibility), nameof(DownloadVisibility),
                                     nameof(CancelVisibility), nameof(UseVisibility), nameof(InUseVisibility),
                                     nameof(DeleteVisibility) })
        {
            OnPropertyChanged(name);
        }
    }
}

/// Downloads whisper.cpp itself and its models into %APPDATA%\Lidiflow.
public sealed class ModelManager : ObservableObject, IModelOwner
{
    // Pinned build: the zip's binaries are executed, so it must match this hash. To update, take a newer
    // build's "whisper-bin-x64.zip" and its digest from https://github.com/ggml-org/whisper.cpp/releases.
    private const string WhisperZipUrl =
        "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";
    private const string WhisperZipSha256 = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb";

    private readonly System.Collections.Generic.Dictionary<string, CancellationTokenSource> downloads = new();
    private string? lastError;
    private bool whisperInstalled;
    private double? whisperProgress;

    public ModelManager()
    {
        Items = new ObservableCollection<ModelItem>
        {
            Item("ggml-base.bin", "Base", "Muito rápido, menos preciso", "142 MB"),
            Item("ggml-small.bin", "Small", "Rápido, bom para frases curtas", "466 MB"),
            Item("ggml-large-v3-turbo-q5_0.bin", "Large v3 Turbo (comprimido)", "Recomendado: ótima precisão e rápido", "547 MB"),
            Item("ggml-medium.bin", "Medium", "Preciso, mais lento", "1,5 GB"),
            Item("ggml-large-v3-turbo.bin", "Large v3 Turbo", "Precisão máxima do Turbo", "1,6 GB"),
            Item("ggml-large-v3.bin", "Large v3", "O mais preciso, o mais lento", "3,1 GB"),
        };
        Refresh();
    }

    public ObservableCollection<ModelItem> Items { get; }

    /// Called when the user picks another installed model.
    public Action<string>? OnUse { get; set; }

    /// Raised after whisper.cpp or a model finishes downloading.
    public event Action? Installed;

    public string? LastError { get => lastError; private set => SetField(ref lastError, value); }
    public bool WhisperInstalled { get => whisperInstalled; private set => SetField(ref whisperInstalled, value); }
    public double? WhisperProgress { get => whisperProgress; private set => SetField(ref whisperProgress, value); }

    private ModelItem Item(string id, string name, string detail, string size) => new(this, id, name, detail, size);

    public void Refresh()
    {
        foreach (var item in Items)
        {
            item.IsInstalled = File.Exists(Path.Combine(Paths.Models, item.Id));
            item.IsSelected = item.Id == AppSettings.Current.LocalModel;
        }
        WhisperInstalled = WhisperServer.FindBinary() != null;
    }

    public async void Download(ModelItem item)
    {
        if (downloads.ContainsKey(item.Id)) return;
        LastError = null;
        var cancel = new CancellationTokenSource();
        downloads[item.Id] = cancel;
        item.Progress = 0;
        try
        {
            var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{item.Id}";
            await Downloader.DownloadAsync(url, Path.Combine(Paths.Models, item.Id), p => item.Progress = p, cancel.Token);
            Refresh();
            Installed?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            LastError = $"Falha ao baixar {item.Name}: {e.Message}";
        }
        finally
        {
            downloads.Remove(item.Id);
            item.Progress = null;
            Refresh();
        }
    }

    public void Cancel(ModelItem item)
    {
        if (downloads.TryGetValue(item.Id, out var cancel)) cancel.Cancel();
    }

    public void Use(ModelItem item) => OnUse?.Invoke(item.Id);

    public void Delete(ModelItem item)
    {
        try
        {
            File.Delete(Path.Combine(Paths.Models, item.Id));
        }
        catch (Exception e)
        {
            LastError = $"Não foi possível apagar {item.Name}: {e.Message}";
        }
        Refresh();
    }

    /// Downloads the official whisper.cpp build for Windows (CPU, x64) and unpacks it.
    public async Task InstallWhisperAsync()
    {
        if (WhisperProgress != null) return;
        LastError = null;
        WhisperProgress = 0;
        var zip = Path.Combine(Paths.Support, "whisper-bin-x64.zip");
        try
        {
            await Downloader.DownloadAsync(WhisperZipUrl, zip, p => WhisperProgress = p, CancellationToken.None);
            if (!await Task.Run(() => HasHash(zip, WhisperZipSha256)))
            {
                LastError = "O pacote do whisper.cpp baixado não é o esperado e foi descartado";
                return;
            }
            var target = Paths.WhisperBinaries;
            await Task.Run(() =>
            {
                Directory.Delete(target, recursive: true);
                ZipFile.ExtractToDirectory(zip, target, overwriteFiles: true);
            });
            if (WhisperServer.FindBinary() == null)
            {
                LastError = "O pacote do whisper.cpp não trouxe o whisper-server.exe";
            }
            else
            {
                Refresh();
                Installed?.Invoke();
            }
        }
        catch (Exception e)
        {
            LastError = "Falha ao instalar o whisper.cpp: " + e.Message;
        }
        finally
        {
            try { File.Delete(zip); } catch { }
            WhisperProgress = null;
            Refresh();
        }
    }

    private static bool HasHash(string path, string sha256)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}

public static class Downloader
{
    /// Streams a file to disk through a temporary ".part" file, reporting progress 0…1 on the caller's thread.
    public static async Task DownloadAsync(string url, string destination, Action<double> progress, CancellationToken token)
    {
        var context = SynchronizationContext.Current;
        void Report(double value)
        {
            if (context != null) context.Post(_ => progress(value), null);
            else progress(value);
        }

        var temp = destination + ".part";
        try
        {
            using var response = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;

            await Task.Run(async () =>
            {
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(temp);
                var buffer = new byte[1 << 16];
                long done = 0;
                double reported = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    if (total > 0 && (double)done / total - reported >= 0.005)
                    {
                        reported = (double)done / total;
                        Report(reported);
                    }
                }
            }, token);

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }
}
