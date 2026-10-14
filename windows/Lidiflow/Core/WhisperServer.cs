using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lidiflow.Core;

/// Runs whisper.cpp's `whisper-server.exe` locally so the model stays loaded between dictations.
public sealed class WhisperServer
{
    private const int Port = 8178;
    private static readonly string BaseUrl = $"http://127.0.0.1:{Port}";

    private Process? process;
    private StreamWriter? log;

    public bool IsRunning => process is { HasExited: false };

    /// The bundled copy, the one the app downloaded, or one on PATH.
    public static string? FindBinary()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "whisper", "whisper-server.exe") };
        try
        {
            candidates.AddRange(Directory.EnumerateFiles(Paths.WhisperBinaries, "whisper-server.exe", SearchOption.AllDirectories));
        }
        catch (IOException) { }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim(), "whisper-server.exe"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;
        var binary = FindBinary() ?? throw new UserFacingException("whisper.cpp não instalado — instale em Modelos");
        var model = AppSettings.Current.ModelPath;
        if (!File.Exists(model)) throw new UserFacingException("Modelo não baixado — escolha um em Modelos");

        // A server left behind by a crashed run would keep the port with the old model.
        if (await IsReachableAsync()) KillOrphans();

        var info = new ProcessStartInfo(binary)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(binary)!,
        };
        foreach (var argument in new[]
                 {
                     "-m", model, "-l", "auto", "--host", "127.0.0.1", "--port", Port.ToString(),
                     "-t", Math.Max(4, Environment.ProcessorCount - 2).ToString(),
                 })
        {
            info.ArgumentList.Add(argument);
        }

        try { log?.Dispose(); } catch { }
        var writer = new StreamWriter(Path.Combine(Paths.Support, "whisper-server.log"), append: false) { AutoFlush = true };
        log = writer;
        void Append(string? line)
        {
            if (line == null) return;
            try { lock (writer) writer.WriteLine(line); } catch { }
        }

        var started = new Process { StartInfo = info };
        started.OutputDataReceived += (_, e) => Append(e.Data);
        started.ErrorDataReceived += (_, e) => Append(e.Data);
        started.Start();
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();
        process = started;

        for (int i = 0; i < 120; i++)
        {
            if (started.HasExited) throw new UserFacingException("whisper-server fechou ao iniciar (veja whisper-server.log)");
            if (await IsReachableAsync()) return;
            await Task.Delay(500);
        }
        throw new UserFacingException("whisper-server não respondeu");
    }

    public void Stop()
    {
        var current = process;
        process = null;
        if (current == null) return;
        try
        {
            if (!current.HasExited)
            {
                current.Kill(entireProcessTree: true);
                current.WaitForExit(3000);
            }
        }
        catch (Exception e)
        {
            Log.Write("Stopping whisper-server: " + e.Message);
        }
        current.Dispose();
    }

    /// Restarts with the currently selected model.
    public async Task RestartAsync()
    {
        Stop();
        await StartAsync();
    }

    public async Task<string> TranscribeAsync(byte[] wav, string language, string? prompt)
    {
        if (!IsRunning) await StartAsync();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0.0"), "temperature");
        form.Add(new StringContent(language), "language");
        if (prompt != null) form.Add(new StringContent(prompt), "prompt");
        form.Add(CloudTranscriber.WavContent(wav), "file", "audio.wav");

        using var deadline = Http.Deadline(120);
        using var response = await Http.Client.PostAsync(BaseUrl + "/inference", form, deadline.Token);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        var text = JsonHelpers.String(() => JsonHelpers.Parse(body)?["text"]);
        if (text == null)
        {
            throw new UserFacingException("Resposta inválida do whisper-server: " + body[..Math.Min(body.Length, 300)]);
        }
        return Clean(text);
    }

    private static async Task<bool> IsReachableAsync()
    {
        try
        {
            using var deadline = Http.Deadline(1);
            using var response = await Http.Client.GetAsync(BaseUrl, deadline.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// Kills whisper-server processes started from the app's own folder.
    private static void KillOrphans()
    {
        foreach (var orphan in Process.GetProcessesByName("whisper-server"))
        {
            try
            {
                var path = orphan.MainModule?.FileName ?? "";
                if (path.StartsWith(Paths.Support, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    orphan.Kill();
                    orphan.WaitForExit(3000);
                }
            }
            catch { }
            finally
            {
                orphan.Dispose();
            }
        }
    }

    /// Drops Whisper's non-speech annotations such as "[BLANK_AUDIO]" or "(música)".
    private static string Clean(string text)
    {
        text = Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)", "");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
