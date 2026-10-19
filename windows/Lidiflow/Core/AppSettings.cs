using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lidiflow.Platform;

namespace Lidiflow.Core;

public enum TranscriptionEngine { Local, Cloud }

public enum Appearance { System, Light, Dark }

/// User preferences in %APPDATA%\Lidiflow\settings.json. API keys live in `Credentials`.
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(Paths.Support, "settings.json");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; } = Load();

    public string Language { get; set; } = "pt";

    /// Up to two shortcuts per action.
    public Dictionary<ShortcutAction, List<KeyCombo>> Shortcuts { get; set; } = ShortcutActions.Defaults();

    public Appearance Theme { get; set; } = Appearance.System;
    public bool SoundsEnabled { get; set; } = true;

    /// The microphone's MMDevice id, or Microphones.Automatic.
    public string Microphone { get; set; } = Platform.Microphones.Automatic;

    /// The dictation bar while idle; it always appears while dictating.
    public bool ShowsDictationBar { get; set; } = true;

    /// Where the dictation bar docks: "bottom", "left" or "right" (see OverlayWindow.Edges).
    public string DictationBarEdge { get; set; } = "bottom";

    /// Master switch for transform shortcuts.
    public bool TransformsEnabled { get; set; } = true;

    /// A transform applied to every dictation, if enabled.
    public bool AutoTransformEnabled { get; set; }
    public Guid? AutoTransformId { get; set; }

    public bool PolishEnabled { get; set; }

    // Transcription

    public TranscriptionEngine Engine { get; set; } = TranscriptionEngine.Local;

    /// File name of the whisper.cpp model in `Paths.Models`.
    public string LocalModel { get; set; } = "ggml-large-v3-turbo-q5_0.bin";

    public LlmProvider TranscriptionProvider { get; set; } = LlmProvider.OpenAI;
    public Dictionary<LlmProvider, string> TranscriptionModels { get; set; } = new();

    // LLM (polish, command mode, transforms)

    public LlmProvider AiProvider { get; set; } = LlmProvider.Ollama;

    /// The cloud AI provider to return to when switching from local back to cloud.
    public LlmProvider LastCloudProvider { get; set; } = LlmProvider.OpenAI;

    /// Chosen model per provider.
    public Dictionary<LlmProvider, string> AiModels { get; set; } = new();

    public string OllamaBaseUrl { get; set; } = "http://localhost:11434/v1";

    public string TranscriptionModel(LlmProvider provider) =>
        TranscriptionModels.TryGetValue(provider, out var model) ? model : CloudTranscriber.Models(provider)[0];

    public string Model(LlmProvider provider) =>
        AiModels.TryGetValue(provider, out var model) ? model : provider.SuggestedModels().FirstOrDefault() ?? "";

    [JsonIgnore]
    public string ModelPath => Path.Combine(Paths.Models, LocalModel);

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            }
        }
        catch (Exception e)
        {
            Log.Write("Could not read settings: " + e.Message);
            DataFile.SetAside(FilePath);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            DataFile.WriteAtomic(FilePath, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        }
        catch (Exception e)
        {
            Log.Write("Could not save settings: " + e.Message);
        }
    }
}
