using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Lidiflow.Core;
using Lidiflow.Platform;
using Lidiflow.UI;

namespace Lidiflow;

/// Tray app that wires the global shortcuts to recording, transcription and insertion.
public sealed class AppController
{
    private enum Mode { Dictation, Command }

    private sealed class Session
    {
        public Mode Mode;
        public readonly DateTime StartedAt = DateTime.Now;
        public bool HandsFree;
        public Task<string?>? Selection;
    }

    private const double MinimumDuration = 0.35;

    private readonly AppModel model = new();
    private readonly WhisperServer whisper = new();
    private readonly AudioRecorder recorder = new();
    private readonly HotkeyMonitor hotkey = new();
    private readonly SemaphoreSlim engineLock = new(1, 1);
    private readonly OverlayWindow overlay = new();
    private MainWindow? mainWindow;
    private TrayIcon? tray;
    private Session? session;
    private int errorToken;

    public void Start()
    {
        AppModel.ApplyTheme(model.Theme);
        tray = new TrayIcon(ShowMainWindow, CopyLast, () => Application.Current.Shutdown());

        recorder.LevelChanged += level => Application.Current.Dispatcher.InvokeAsync(() => overlay.Level = level);
        hotkey.OnAction = Handle;
        hotkey.IsRecording = () => session != null;
        hotkey.IsHandsFree = () => session?.HandsFree == true;
        hotkey.TransformIndex = (code, mods, baseDown) =>
        {
            if (!model.TransformsEnabled) return null;
            for (int i = 0; i < model.Transforms.Count; i++)
            {
                // Positional defaults only apply while the base key is held.
                if (model.Transforms[i].Shortcut == null && !baseDown) continue;
                if (model.ShortcutFor(i)?.Matches(code, mods) == true) return i;
            }
            return null;
        };
        hotkey.Shortcuts = model.Shortcuts;
        model.ShortcutsChanged += () =>
        {
            hotkey.Shortcuts = model.Shortcuts;
            if (model.IsReady) SetReady();
        };
        model.EngineChanged += () => _ = PrepareEngineAsync();
        model.RecordShortcut = callback => hotkey.Record(callback);
        model.CancelShortcutRecording = hotkey.CancelRecording;
        model.Models.OnUse = id => model.LocalModel = id;
        model.Models.Installed += () =>
        {
            if (!model.IsReady && model.Engine == TranscriptionEngine.Local) _ = PrepareEngineAsync();
        };
        model.Ollama.OnUse = id =>
        {
            model.AiProvider = LlmProvider.Ollama;
            model.AiModel = id;
            _ = model.Ollama.RefreshAsync();
        };

        if (!hotkey.Start()) SetStatus("Não foi possível ativar os atalhos globais");
        ShowMainWindow();
        _ = PrepareEngineAsync();
    }

    public void Shutdown()
    {
        hotkey.Dispose();
        whisper.Stop();
        tray?.Dispose();
    }

    public void ShowMainWindow()
    {
        mainWindow ??= new MainWindow(model);
        mainWindow.ShowAndActivate();
    }

    /// Starts or stops the local server to match the selected transcription engine.
    private async Task PrepareEngineAsync()
    {
        await engineLock.WaitAsync();
        try
        {
            model.IsReady = false;
            if (model.Engine == TranscriptionEngine.Cloud)
            {
                whisper.Stop();
                var provider = model.TranscriptionProvider;
                if (!model.HasKey(provider))
                {
                    SetStatus($"Adicione sua chave da {provider.Title()} em Modelos");
                    return;
                }
            }
            else
            {
                SetStatus("Carregando modelo…");
                try
                {
                    await whisper.RestartAsync();
                }
                catch (Exception e)
                {
                    SetStatus(e is UserFacingException ? e.Message : "Falha ao iniciar o whisper-server");
                    Log.Write("whisper-server: " + e);
                    return;
                }
            }
            SetReady();
        }
        finally
        {
            engineLock.Release();
        }
    }

    // MARK: - Shortcuts

    private void Handle(HotkeyEvent e)
    {
        switch (e.Kind)
        {
            case HotkeyKind.HoldStart:
                Begin(e.Action == ShortcutAction.Command ? Mode.Command : Mode.Dictation);
                break;
            case HotkeyKind.SwitchToCommand:
                SwitchToCommand();
                break;
            case HotkeyKind.HoldEnd:
                if (session is { HandsFree: false }) _ = FinishAsync();
                break;
            case HotkeyKind.HandsFree:
                ToggleHandsFree();
                break;
            case HotkeyKind.Stop:
                _ = FinishAsync();
                break;
            case HotkeyKind.Cancel:
                Cancel();
                break;
            case HotkeyKind.Discard:
                Discard();
                break;
            case HotkeyKind.PasteLast:
                if (model.History.Count > 0) _ = PasteLastAsync(model.History[0].Text);
                break;
            case HotkeyKind.CopyLast:
                CopyLast();
                break;
            case HotkeyKind.Transform:
                if (session is { HandsFree: false }) Discard();
                _ = RunTransformAsync(e.Index);
                break;
        }
    }

    /// Starts a hands-free dictation, locks the one being held, or stops a hands-free one.
    private void ToggleHandsFree()
    {
        if (session == null)
        {
            Begin(Mode.Dictation);
            if (session == null) return;
            session.HandsFree = true;
            ShowOverlay(OverlayPhase.HandsFree);
            return;
        }
        if (session.HandsFree)
        {
            _ = FinishAsync();
            return;
        }
        session.HandsFree = true;
        ShowOverlay(session.Mode == Mode.Command ? OverlayPhase.Command : OverlayPhase.HandsFree);
        Play(Sound.Lock);
    }

    // MARK: - Dictation

    private void Begin(Mode mode)
    {
        if (session != null) return;
        try
        {
            recorder.Start();
        }
        catch (Exception e)
        {
            Log.Write("Microphone: " + e.Message);
            FlashError("Microfone indisponível");
            return;
        }
        session = new Session { Mode = mode };
        if (mode == Mode.Command) CaptureSelection();
        ShowOverlay(mode == Mode.Command ? OverlayPhase.Command : OverlayPhase.Listening);
        Play(Sound.Start);
    }

    private void SwitchToCommand()
    {
        if (session == null) return;
        session.Mode = Mode.Command;
        CaptureSelection();
        ShowOverlay(OverlayPhase.Command);
    }

    private async Task PasteLastAsync(string text)
    {
        try
        {
            await TextInserter.InsertAsync(text);
        }
        catch (Exception e)
        {
            FlashError(Message(e));
        }
    }

    private void CaptureSelection()
    {
        if (session != null) session.Selection = TextInserter.SelectedTextAsync();
    }

    private void Cancel()
    {
        if (session == null) return;
        Discard();
        Play(Sound.Cancel);
    }

    private void Discard()
    {
        if (session == null) return;
        session = null;
        _ = recorder.StopAsync();
        ShowOverlay(OverlayPhase.Hidden);
    }

    /// Applies a saved transform to the focused app's selected text.
    private async Task RunTransformAsync(int index)
    {
        if (index < 0 || index >= model.Transforms.Count)
        {
            FlashError($"Nenhum transform no {index + 1}");
            return;
        }
        var transform = model.Transforms[index];
        var text = await TextInserter.SelectedTextAsync();
        if (text == null)
        {
            FlashError("Selecione um texto primeiro");
            return;
        }
        ShowOverlay(OverlayPhase.Transforming, transform.Name);
        try
        {
            var result = await Llm.TransformAsync(text, transform.Prompt);
            ShowOverlay(OverlayPhase.Hidden);
            await TextInserter.InsertAsync(result);
        }
        catch (Exception e)
        {
            FlashError(Message(e));
        }
    }

    private async Task FinishAsync()
    {
        var current = session;
        if (current == null) return;
        session = null;
        var samples = await recorder.StopAsync();
        var duration = (DateTime.Now - current.StartedAt).TotalSeconds;

        if (duration < MinimumDuration || samples.Length == 0)
        {
            ShowOverlay(OverlayPhase.Hidden);
            return;
        }
        Play(Sound.Stop);
        ShowOverlay(OverlayPhase.Transcribing);

        try
        {
            var spoken = await TranscribeAsync(AudioRecorder.Wav(samples));
            if (string.IsNullOrWhiteSpace(spoken))
            {
                ShowOverlay(OverlayPhase.Hidden);
                return;
            }

            string text;
            if (current.Mode == Mode.Dictation)
            {
                text = await ProcessDictationAsync(spoken);
            }
            else
            {
                ShowOverlay(OverlayPhase.Thinking);
                var selection = current.Selection == null ? null : await current.Selection;
                text = await Llm.CommandAsync(spoken, selection);
            }

            model.AddDictation(new Dictation { Date = DateTime.Now, Text = text, Duration = duration });
            ShowOverlay(OverlayPhase.Hidden);
            await TextInserter.InsertAsync(text);
        }
        catch (Exception e)
        {
            FlashError(Message(e));
            Log.Write("Dictation: " + e);
        }
    }

    private Task<string> TranscribeAsync(byte[] wav)
    {
        var prompt = TextProcessor.WhisperPrompt(model.Words);
        return model.Engine == TranscriptionEngine.Local
            ? whisper.TranscribeAsync(wav, model.Language, prompt)
            : CloudTranscriber.TranscribeAsync(wav, model.Language, prompt);
    }

    private static string Message(Exception e) => e is UserFacingException ? e.Message : "Falha na transcrição";

    private async Task<string> ProcessDictationAsync(string spoken)
    {
        var text = spoken;
        if (model.PolishEnabled)
        {
            ShowOverlay(OverlayPhase.Polishing);
            try { text = await Llm.PolishAsync(text); } catch (Exception e) { Log.Write("Polish: " + e.Message); }
        }
        if (model.AutoTransform is { } transform)
        {
            ShowOverlay(OverlayPhase.Transforming, transform.Name);
            try { text = await Llm.TransformAsync(text, transform.Prompt); } catch (Exception e) { Log.Write("Auto transform: " + e.Message); }
        }
        return TextProcessor.Apply(text, model.Replacements, model.Snippets);
    }

    private void ShowOverlay(OverlayPhase phase, string? detail = null)
    {
        errorToken++;
        overlay.Display(phase, detail);
    }

    private async void FlashError(string message)
    {
        ShowOverlay(OverlayPhase.Error, message);
        int token = errorToken;
        await Task.Delay(2500);
        if (token == errorToken) ShowOverlay(OverlayPhase.Hidden);
    }

    // MARK: - Sounds

    private enum Sound { Start, Stop, Cancel, Lock }

    private readonly Dictionary<Sound, SoundPlayer?> players = new();

    private void Play(Sound sound)
    {
        if (!model.SoundsEnabled) return;
        if (!players.TryGetValue(sound, out var player))
        {
            var file = sound switch
            {
                Sound.Start => "Speech On.wav",
                Sound.Stop => "Speech Off.wav",
                Sound.Cancel => "Speech Sleep.wav",
                _ => "Windows Navigation Start.wav",
            };
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", file);
            player = File.Exists(path) ? new SoundPlayer(path) : null;
            players[sound] = player;
        }
        try { player?.Play(); } catch { }
    }

    // MARK: - Status

    private void SetReady()
    {
        model.IsReady = true;
        SetStatus($"Pronto — segure {model.DictateLabel} para ditar");
    }

    private void SetStatus(string text)
    {
        model.Status = text;
        if (tray != null) tray.Status = text;
    }

    private void CopyLast()
    {
        if (model.History.Count == 0) return;
        try { Clipboard.SetText(model.History[0].Text); } catch (Exception e) { Log.Write("Copy: " + e.Message); }
    }
}
