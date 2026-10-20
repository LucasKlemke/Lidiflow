using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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

    // Meetings (docs/reunioes.md).
    private MeetingProcessor? processor;
    private MeetingRecorder? meetingRecorder;
    private DateTime meetingStartedAt;
    private readonly DispatcherTimer meetingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private LiveTranscriber? live;
    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly MeetingDetector detector = new();
    private static readonly System.Windows.Media.Color ReadyColor = System.Windows.Media.Color.FromRgb(0x7F, 0xA8, 0x8A);
    /// The call whose start offer was used or dismissed; a new call offers again.
    private string? offeredCall;

    public void Start()
    {
        AppModel.ApplyTheme(model.Theme);
        tray = new TrayIcon(ShowMainWindow, CopyLast, () => Application.Current.Shutdown());

        recorder.LevelChanged += level => Application.Current.Dispatcher.InvokeAsync(() => overlay.Level = level);
        SetUpDictationBar();
        SetUpMeetings();
        SetUpMeetingDetection();
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
            overlay.DictateKeys = DictateKeys();
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
        _ = StartEnginesAsync();
    }

    /// Meetings left half-processed by the last run continue once the transcription engine is up.
    private async Task StartEnginesAsync()
    {
        await PrepareEngineAsync();
        ResumeMeetings();
    }

    // MARK: - Meetings

    private void SetUpMeetings()
    {
        processor = new MeetingProcessor(TranscribeAsync,
            text => TextProcessor.Apply(text, model.Replacements, Array.Empty<Snippet>()));
        model.ToggleMeeting = ToggleMeeting;
        model.ResummarizeMeeting = meeting => _ = processor.ResummarizeAsync(meeting);
        tray?.SetMeetingAction(ToggleMeeting);
        overlay.OnToggleMeeting = ToggleMeeting;
        overlay.IsMeetingRecording = () => model.IsMeetingRecording;
        meetingTimer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - meetingStartedAt;
            model.MeetingElapsed = elapsed;
            overlay.MeetingElapsed = elapsed;
        };
        liveTimer.Tick += (_, _) => { if (live is { } current) _ = current.TickAsync(); };
    }

    /// Meetings left unfinished by a quit (or a crash) are processed again, one at a time.
    private void ResumeMeetings()
    {
        if (processor == null) return;
        foreach (var meeting in model.Meetings.Where(m => m != model.RecordingMeeting
                     && m.Status is Meeting.Recording or Meeting.Transcribing or Meeting.Separating or Meeting.Summarizing).ToList())
        {
            _ = processor.ProcessAsync(meeting);
        }
    }

    private async void ToggleMeeting()
    {
        if (model.RecordingMeeting is { } meeting) await StopMeetingAsync(meeting);
        else StartMeeting();
    }

    /// Offers to record when a call starts, and to stop when it ends (docs/reunioes.md, "Fase 2").
    private void SetUpMeetingDetection()
    {
        detector.Enabled = () => model.DetectMeetings;
        detector.CallStarted += app =>
        {
            // "Gravar sozinho": record right away, no offer.
            if (model.AutoRecordMeetings)
            {
                if (model.RecordingMeeting == null) StartMeeting(app);
                return;
            }
            if (model.RecordingMeeting != null || offeredCall == app) return;
            offeredCall = app;
            overlay.ShowCard(new OverlayWindow.Card("\uE714", "Reunião detectada", $"{app} · agora", "Gravar",
                Run: () => { if (model.RecordingMeeting == null) StartMeeting(app); },
                Dismissed: () => { })
            {
                Dot = ReadyColor, CompactSubtitle = "agora", ActionShowsSymbol = true,
            }, TimeSpan.FromSeconds(20));
        };
        detector.CallEnded += async app =>
        {
            offeredCall = null;
            overlay.ClearOffer();
            if (model.RecordingMeeting is not { } recording) return;
            if (model.AutoRecordMeetings)
            {
                await StopMeetingAsync(recording);
                return;
            }
            var recorded = Meeting.Clock((DateTime.Now - meetingStartedAt).TotalSeconds);
            overlay.ShowCard(new OverlayWindow.Card("\uE71A", "A chamada acabou", $"{app} · {recorded} gravados", "Parar e resumir",
                Run: async () => { if (model.RecordingMeeting == recording) await StopMeetingAsync(recording); },
                Dismissed: () => { })
            {
                Dot = System.Windows.Media.Color.FromRgb(0x1F, 0x6B, 0xFF),
            }, TimeSpan.FromSeconds(30));
        };
        detector.Start();
    }

    private void StartMeeting(string? app = null)
    {
        var now = DateTimeOffset.Now;
        var meeting = Meeting.New(now, app);
        var recording = new MeetingRecorder(meeting.MicPath, meeting.SystemPath);
        try
        {
            System.IO.Directory.CreateDirectory(meeting.Folder);
            recording.Start();
        }
        catch (Exception e)
        {
            Log.Write("Meeting start: " + e);
            MeetingStore.Delete(meeting);
            FlashError("Não foi possível gravar a reunião");
            return;
        }
        meeting.SystemAudio = recording.SystemAudio;
        overlay.ClearOffer();
        MeetingStore.Save(meeting);
        meetingRecorder = recording;
        meetingStartedAt = DateTime.Now;
        // The live transcript, from the same audio as the files (docs/reunioes.md, "Transcrição ao vivo").
        var transcriber = new LiveTranscriber(TranscribeAsync);
        transcriber.Changed += utterances => overlay.LiveUtterances = utterances;
        recording.Captured = transcriber.Append;
        live = transcriber;
        liveTimer.Start();
        model.Meetings.Insert(0, meeting);
        model.RecordingMeeting = meeting;
        model.MeetingElapsed = TimeSpan.Zero;
        overlay.MeetingElapsed = TimeSpan.Zero;
        tray?.SetMeetingRecording(true);
        meetingTimer.Start();
        Play(Sound.Lock);
    }

    private async Task StopMeetingAsync(Meeting meeting)
    {
        var recording = meetingRecorder;
        meetingRecorder = null;
        meetingTimer.Stop();
        liveTimer.Stop();
        int words = live?.WordCount ?? 0;
        live?.Stop();
        live = null;
        if (recording != null) recording.Captured = null;
        overlay.ClearOffer();
        model.RecordingMeeting = null;
        overlay.MeetingElapsed = null;
        tray?.SetMeetingRecording(false);
        meeting.Duration = (DateTime.Now - meetingStartedAt).TotalSeconds;
        Play(Sound.Stop);
        if (words < 15 && meeting.Duration < 120) AskIfAccidental(meeting);
        if (recording != null) await recording.StopAsync();
        if (meeting.Discarded)
        {
            // Discarded while the files were still open: the folder could not go then.
            MeetingStore.Delete(meeting);
            return;
        }
        meeting.Status = Meeting.Transcribing;
        MeetingStore.Save(meeting);
        if (processor != null) await processor.ProcessAsync(meeting);
    }

    /// "Começou sem querer?": almost nothing was heard. Descartar deletes the meeting, even while it is processed;
    /// Manter or the 15 s running out leave it.
    private void AskIfAccidental(Meeting meeting)
    {
        overlay.ShowCard(new OverlayWindow.Card("\uE7BA", "Começou sem querer?",
            "Só algumas palavras foram captadas. Manter ou descartar a reunião?", "Manter",
            Run: () => { },
            Dismissed: () => { })
        {
            SecondaryLabel = "Descartar",
            Secondary = () => model.DeleteMeeting(meeting),
            Dismissable = false,
        }, TimeSpan.FromSeconds(15));
    }

    /// The bar's buttons and menu, and the settings it follows.
    private void SetUpDictationBar()
    {
        overlay.DictateKeys = DictateKeys();
        overlay.OnDictate = ToggleHandsFree;
        overlay.OnStop = () => { if (session != null) _ = FinishAsync(); };
        overlay.OnCancel = Cancel;
        // Copying is the person's choice here, so it replaces the clipboard on purpose.
        overlay.OnCopyUnplaced = async () =>
        {
            if (model.History.Count == 0) return;
            try { System.Windows.Clipboard.SetText(model.History[0].Text); }
            catch (Exception e) { Log.Write("Copy dictation: " + e.Message); return; }
            int token = errorToken;
            await Task.Delay(1200);
            if (token == errorToken) ShowOverlay(OverlayPhase.Hidden);
        };
        overlay.OnDismissUnplaced = () => ShowOverlay(OverlayPhase.Hidden);
        overlay.OnPasteLast = () => { if (model.History.Count > 0) _ = PasteLastAsync(model.History[0].Text); };
        overlay.OnOpenApp = ShowMainWindow;
        overlay.CurrentMicrophone = () => model.Microphone;
        overlay.OnSelectMicrophone = id => model.Microphone = id;
        overlay.OnEdgeChanged = edge => model.DictationBarEdge = edge;
        overlay.Edge = model.DictationBarEdge;
        overlay.ShowsIdleBar = model.ShowsDictationBar;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppModel.ShowsDictationBar)) overlay.ShowsIdleBar = model.ShowsDictationBar;
        };
        overlay.Display(OverlayPhase.Hidden);
    }

    private List<string> DictateKeys() => model.CombosFor(ShortcutAction.Dictate).FirstOrDefault()?.Keys ?? new List<string>();

    public void Shutdown()
    {
        detector.Stop();
        live?.Stop();
        // Quitting mid-meeting closes the files; the transcription runs on the next launch.
        if (meetingRecorder is { } recording && model.RecordingMeeting is { } meeting)
        {
            meeting.Duration = (DateTime.Now - meetingStartedAt).TotalSeconds;
            Task.Run(recording.StopAsync).Wait(3000);
            meeting.Status = Meeting.Transcribing;
            MeetingStore.Save(meeting);
        }
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
            // Paste only into a text box; otherwise keep it, without touching the clipboard, for the paste shortcut.
            if (await TextInserter.FocusedTargetAsync() == TextInserter.Target.NotEditable)
            {
                ShowUnplaced(text);
            }
            else
            {
                ShowOverlay(OverlayPhase.Hidden);
                await TextInserter.InsertAsync(text);
            }
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

    /// The card stays 12 s, longer while the mouse is on it, so the text can be read and copied.
    private async void ShowUnplaced(string text)
    {
        overlay.PasteKeys = model.CombosFor(ShortcutAction.PasteLast).FirstOrDefault()?.Keys ?? new List<string>();
        ShowOverlay(OverlayPhase.Unplaced, text);
        int token = errorToken;
        await Task.Delay(12000);
        while (token == errorToken && overlay.IsMouseOver) await Task.Delay(3000);
        if (token == errorToken) ShowOverlay(OverlayPhase.Hidden);
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
