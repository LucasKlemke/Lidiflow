using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Lidiflow.Core;
using Lidiflow.Platform;
using Microsoft.Win32;

namespace Lidiflow;

/// The shortcut slot being edited: an action's, or a transform's (null index for a new one).
public abstract record ShortcutSlot;
public sealed record ActionSlot(ShortcutAction Action) : ShortcutSlot;
public sealed record TransformSlot(int? Index) : ShortcutSlot;

/// App state shared by the controller and the UI. Preferences go to AppSettings; history,
/// vocabulary and transforms to their own JSON files, like the macOS app. The history is
/// encrypted for the current Windows user, since it holds everything the user dictated.
public sealed class AppModel : ObservableObject
{
    private readonly AppSettings settings = AppSettings.Current;
    private readonly string historyPath = Path.Combine(Paths.Support, "history.dat");
    /// Plain-text history from earlier versions, encrypted and removed on first launch.
    private readonly string legacyHistoryPath = Path.Combine(Paths.Support, "history.json");
    private readonly string vocabularyPath = Path.Combine(Paths.Support, "vocabulary.json");
    private readonly string transformsPath = Path.Combine(Paths.Support, "transforms.json");

    private string status = "Iniciando…";
    private bool isReady;

    private sealed class Vocabulary
    {
        public List<string> Dictionary { get; set; } = new();
        public List<Replacement> Replacements { get; set; } = new();
        public List<Snippet> Snippets { get; set; } = new();
    }

    public AppModel()
    {
        History = new ObservableCollection<Dictation>(ReadHistory());
        var vocabulary = Read<Vocabulary>(vocabularyPath) ?? new Vocabulary();
        Words = new ObservableCollection<string>(vocabulary.Dictionary);
        Replacements = new ObservableCollection<Replacement>(vocabulary.Replacements);
        Snippets = new ObservableCollection<Snippet>(vocabulary.Snippets);
        Transforms = new ObservableCollection<Transform>(Read<List<Transform>>(transformsPath) ?? Transform.Defaults());
        RefreshTransformLabels();
    }

    public ObservableCollection<Dictation> History { get; }
    /// Custom words and names (the "dictionary" in the UI), also sent to Whisper as a hint.
    public ObservableCollection<string> Words { get; }
    public ObservableCollection<Replacement> Replacements { get; }
    public ObservableCollection<Snippet> Snippets { get; }
    public ObservableCollection<Transform> Transforms { get; }

    public ModelManager Models { get; } = new();
    public OllamaManager Ollama { get; } = new();

    // MARK: - Meetings (docs/reunioes.md)

    private Meeting? recordingMeeting;
    private TimeSpan meetingElapsed;

    /// Newest first, as on disk.
    public ObservableCollection<Meeting> Meetings { get; } = new(MeetingStore.LoadAll());

    /// The meeting being recorded, if any.
    public Meeting? RecordingMeeting
    {
        get => recordingMeeting;
        set { if (SetField(ref recordingMeeting, value)) OnPropertyChanged(nameof(IsMeetingRecording)); }
    }

    public bool IsMeetingRecording => recordingMeeting != null;

    public TimeSpan MeetingElapsed { get => meetingElapsed; set => SetField(ref meetingElapsed, value); }

    /// Set by the controller: start or stop recording a meeting; write a meeting's summary again.
    public Action? ToggleMeeting { get; set; }
    public Action<Meeting>? ResummarizeMeeting { get; set; }

    public void DeleteMeeting(Meeting meeting)
    {
        if (meeting == recordingMeeting) return;
        // Also while it is being processed: the processor sees Discarded and stops (see MeetingProcessor).
        MeetingStore.Delete(meeting);
        Meetings.Remove(meeting);
    }

    public event Action? ShortcutsChanged;
    /// Raised when the transcription engine, local model or its API key changes.
    public event Action? EngineChanged;

    /// Set by the controller: capture the next shortcut pressed (null when cancelled).
    public Action<Action<KeyCombo?>>? RecordShortcut { get; set; }
    public Action? CancelShortcutRecording { get; set; }

    public string Status { get => status; set => SetField(ref status, value); }
    public bool IsReady { get => isReady; set => SetField(ref isReady, value); }

    // MARK: - Preferences

    public string Language
    {
        get => settings.Language;
        set { settings.Language = value; Save(); }
    }

    public Appearance Theme
    {
        get => settings.Theme;
        set { settings.Theme = value; Save(); ApplyTheme(value); }
    }

    public string Microphone
    {
        get => settings.Microphone;
        set { settings.Microphone = value; Save(); }
    }

    public bool ShowsDictationBar
    {
        get => settings.ShowsDictationBar;
        set { settings.ShowsDictationBar = value; Save(); OnPropertyChanged(); }
    }

    public string DictationBarEdge
    {
        get => settings.DictationBarEdge;
        set { settings.DictationBarEdge = value; Save(); }
    }

    public bool DetectMeetings
    {
        get => settings.DetectMeetings;
        set { settings.DetectMeetings = value; Save(); }
    }

    public bool AutoRecordMeetings
    {
        get => settings.AutoRecordMeetings;
        set { settings.AutoRecordMeetings = value; Save(); OnPropertyChanged(); }
    }

    public bool AutoRecordAsked
    {
        get => settings.AutoRecordAsked;
        set { settings.AutoRecordAsked = value; Save(); OnPropertyChanged(); }
    }

    /// Set by the main window: opens Ajustes (the gear on the Reuniões page).
    public Action? ShowSettings { get; set; }

    public bool SoundsEnabled
    {
        get => settings.SoundsEnabled;
        set { settings.SoundsEnabled = value; Save(); }
    }

    public bool PolishEnabled
    {
        get => settings.PolishEnabled;
        set { settings.PolishEnabled = value; Save(); }
    }

    public bool TransformsEnabled
    {
        get => settings.TransformsEnabled;
        set { settings.TransformsEnabled = value; Save(); }
    }

    public bool AutoTransformEnabled
    {
        get => settings.AutoTransformEnabled;
        set { settings.AutoTransformEnabled = value; Save(); }
    }

    public Guid? AutoTransformId
    {
        get => settings.AutoTransformId;
        set { settings.AutoTransformId = value; Save(); }
    }

    /// The transform applied after every dictation, when auto-apply is on.
    public Transform? AutoTransform =>
        TransformsEnabled && AutoTransformEnabled
            ? Transforms.FirstOrDefault(t => t.Id == AutoTransformId) ?? Transforms.FirstOrDefault()
            : null;

    public TranscriptionEngine Engine
    {
        get => settings.Engine;
        set { settings.Engine = value; Save(); EngineChanged?.Invoke(); }
    }

    public string LocalModel
    {
        get => settings.LocalModel;
        set { settings.LocalModel = value; Save(); Models.Refresh(); EngineChanged?.Invoke(); }
    }

    public LlmProvider TranscriptionProvider
    {
        get => settings.TranscriptionProvider;
        set
        {
            settings.TranscriptionProvider = value;
            Save();
            OnPropertyChanged(nameof(TranscriptionModel));
            EngineChanged?.Invoke();
        }
    }

    public string TranscriptionModel
    {
        get => settings.TranscriptionModel(TranscriptionProvider);
        set { settings.TranscriptionModels[TranscriptionProvider] = value; Save(); }
    }

    public LlmProvider AiProvider
    {
        get => settings.AiProvider;
        set
        {
            settings.AiProvider = value;
            if (value != LlmProvider.Ollama) settings.LastCloudProvider = value;
            Save();
            OnPropertyChanged(nameof(AiModel));
            OnPropertyChanged(nameof(AiRunsLocally));
        }
    }

    /// Whether the AI runs on this PC (Ollama) or on a cloud account.
    public bool AiRunsLocally
    {
        get => AiProvider == LlmProvider.Ollama;
        set => AiProvider = value ? LlmProvider.Ollama : settings.LastCloudProvider;
    }

    public string AiModel
    {
        get => settings.Model(AiProvider);
        set { settings.AiModels[AiProvider] = value; Save(); }
    }

    public string OllamaBaseUrl
    {
        get => settings.OllamaBaseUrl;
        set { settings.OllamaBaseUrl = value; Save(); }
    }

    /// Starts Lidiflow when the user signs in to Windows.
    public bool LaunchAtStartup
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Lidiflow") != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Lidiflow", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("Lidiflow", throwOnMissingValue: false);
            OnPropertyChanged();
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        settings.Save();
        OnPropertyChanged(name);
    }

    public static void ApplyTheme(Appearance theme)
    {
        Application.Current.ThemeMode = theme switch
        {
            Appearance.Light => ThemeMode.Light,
            Appearance.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    // MARK: - API keys

    public bool HasKey(LlmProvider provider) => Credentials.Get(provider) != null;

    public void SaveKey(string key, LlmProvider provider)
    {
        var trimmed = key.Trim();
        Credentials.Set(provider, trimmed.Length == 0 ? null : trimmed);
        if (provider == TranscriptionProvider && Engine == TranscriptionEngine.Cloud) EngineChanged?.Invoke();
    }

    // MARK: - History

    public void AddDictation(Dictation dictation)
    {
        History.Insert(0, dictation);
        SaveHistory();
    }

    public void DeleteDictation(Dictation dictation)
    {
        History.Remove(dictation);
        SaveHistory();
    }

    public void ClearHistory()
    {
        History.Clear();
        SaveHistory();
    }

    private void SaveHistory()
    {
        WriteEncrypted(historyPath, History.ToList());
        OnPropertyChanged(nameof(TotalWords));
        OnPropertyChanged(nameof(StreakDays));
        OnPropertyChanged(nameof(WordsPerMinute));
    }

    public int TotalWords => History.Sum(d => d.Words);

    /// Consecutive days with at least one dictation, ending today or yesterday.
    public int StreakDays
    {
        get
        {
            var days = History.Select(d => d.Date.Date).ToHashSet();
            var day = DateTime.Today;
            if (!days.Contains(day)) day = day.AddDays(-1);
            int streak = 0;
            while (days.Contains(day))
            {
                streak++;
                day = day.AddDays(-1);
            }
            return streak;
        }
    }

    public int WordsPerMinute
    {
        get
        {
            var minutes = History.Sum(d => d.Duration) / 60;
            return minutes > 0 ? (int)(TotalWords / minutes) : 0;
        }
    }

    // MARK: - Vocabulary and transforms

    public void SaveVocabulary() => Write(vocabularyPath, new Vocabulary
    {
        Dictionary = Words.ToList(),
        Replacements = Replacements.ToList(),
        Snippets = Snippets.ToList(),
    });

    public void SaveTransforms()
    {
        Write(transformsPath, Transforms.ToList());
        RefreshTransformLabels();
    }

    public void RefreshTransformLabels()
    {
        for (int i = 0; i < Transforms.Count; i++)
        {
            Transforms[i].ShortcutLabel = ShortcutFor(i)?.Label ?? "";
        }
    }

    // MARK: - Shortcuts

    public Dictionary<ShortcutAction, List<KeyCombo>> Shortcuts => settings.Shortcuts;

    public List<KeyCombo> CombosFor(ShortcutAction action) =>
        Shortcuts.TryGetValue(action, out var combos) ? combos : new List<KeyCombo>();

    public void SetShortcuts(ShortcutAction action, List<KeyCombo> combos)
    {
        settings.Shortcuts[action] = combos;
        ShortcutsUpdated();
    }

    public void ResetShortcuts()
    {
        settings.Shortcuts = ShortcutActions.Defaults();
        ShortcutsUpdated();
    }

    private void ShortcutsUpdated()
    {
        settings.Save();
        RefreshTransformLabels();
        OnPropertyChanged(nameof(DictateLabel));
        ShortcutsChanged?.Invoke();
    }

    /// The transform's own shortcut, or dictation key + its position for the first nine.
    public KeyCombo? ShortcutFor(int index)
    {
        if (index < 0 || index >= Transforms.Count) return null;
        return Transforms[index].Shortcut ?? DefaultShortcut(index);
    }

    /// Dictation key + position, when dictation is held with modifiers alone (e.g. Ctrl + Win + 1).
    public KeyCombo? DefaultShortcut(int index)
    {
        if (index < 0 || index >= HotkeyMonitor.Digits.Length) return null;
        var baseCombo = CombosFor(ShortcutAction.Dictate).FirstOrDefault(c => c.IsModifierOnly);
        return baseCombo == null ? null : KeyCombo.Key(HotkeyMonitor.Digits[index], baseCombo.Modifiers, $"{index + 1}");
    }

    /// The dictation shortcut as text, e.g. "Ctrl + Win".
    public string DictateLabel
    {
        get
        {
            var combo = CombosFor(ShortcutAction.Dictate).FirstOrDefault();
            return combo == null ? "o atalho" : combo.Label;
        }
    }

    /// Why a combo can't be used in `slot`, if it can't.
    public string? Conflict(KeyCombo combo, ShortcutSlot slot)
    {
        ShortcutAction? editedAction = null;
        int? editedTransform = null;
        switch (slot)
        {
            case ActionSlot action:
                editedAction = action.Action;
                break;
            case TransformSlot transform:
                editedTransform = transform.Index;
                if (combo.IsModifierOnly) return "Transforms precisam de uma tecla ou botão do mouse";
                break;
        }
        if (!combo.IsUsable(allowPlainKeys: editedAction == ShortcutAction.Cancel))
        {
            return "Use um modificador (Ctrl, Alt, Shift, Win), uma tecla F ou um botão do mouse";
        }
        foreach (var (action, combos) in Shortcuts)
        {
            if (!combos.Contains(combo)) continue;
            return action == editedAction ? "Esse atalho já está nesta ação" : $"Já usado para {action.Title().ToLowerInvariant()}";
        }
        for (int i = 0; i < Transforms.Count; i++)
        {
            if (i != editedTransform && Equals(ShortcutFor(i), combo))
            {
                return $"Já usado pelo transform “{Transforms[i].Name}”";
            }
        }
        return null;
    }

    // MARK: - Files

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), AppSettings.Json) : null;
        }
        catch (Exception e)
        {
            Log.Write($"Could not read {Path.GetFileName(path)}: {e.Message}");
            DataFile.SetAside(path);
            return null;
        }
    }

    private List<Dictation> ReadHistory()
    {
        if (File.Exists(historyPath)) return ReadEncrypted<List<Dictation>>(historyPath) ?? new List<Dictation>();

        var legacy = Read<List<Dictation>>(legacyHistoryPath);
        if (legacy == null) return new List<Dictation>();
        WriteEncrypted(historyPath, legacy);
        if (File.Exists(historyPath))
        {
            try { File.Delete(legacyHistoryPath); } catch (Exception e) { Log.Write("Could not delete history.json: " + e.Message); }
        }
        return legacy;
    }

    /// Reads a file encrypted with DPAPI: only this Windows user, on this PC, can decrypt it.
    private static T? ReadEncrypted<T>(string path) where T : class
    {
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(json, AppSettings.Json);
        }
        catch (Exception e)
        {
            Log.Write($"Could not read {Path.GetFileName(path)}: {e.Message}");
            DataFile.SetAside(path);
            return null;
        }
    }

    private static void WriteEncrypted<T>(string path, T value)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(value, AppSettings.Json);
            DataFile.WriteAtomic(path, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e)
        {
            Log.Write($"Could not save {Path.GetFileName(path)}: {e.Message}");
        }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            DataFile.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, AppSettings.Json));
        }
        catch (Exception e)
        {
            Log.Write($"Could not save {Path.GetFileName(path)}: {e.Message}");
        }
    }
}
