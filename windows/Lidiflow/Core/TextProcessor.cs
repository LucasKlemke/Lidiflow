using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lidiflow.Platform;

namespace Lidiflow.Core;

public sealed class Replacement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class Snippet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Trigger { get; set; } = "";
    public string Expansion { get; set; } = "";
}

public sealed class Dictation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Date { get; set; }
    public string Text { get; set; } = "";
    /// Seconds spoken.
    public double Duration { get; set; }

    [JsonIgnore]
    public int Words => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [JsonIgnore]
    public string DateLabel => Date.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
}

/// A saved instruction that rewrites text. Without its own shortcut, the first nine
/// are bound to the dictation key + 1…9 by position.
public sealed class Transform : ObservableObject
{
    private string name = "";
    private string prompt = "";
    private KeyCombo? shortcut;
    private string shortcutLabel = "";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => name; set => SetField(ref name, value); }
    public string Prompt { get => prompt; set => SetField(ref prompt, value); }
    public KeyCombo? Shortcut { get => shortcut; set => SetField(ref shortcut, value); }

    /// The effective shortcut (own or positional), filled in by AppModel for display.
    [JsonIgnore]
    public string ShortcutLabel { get => shortcutLabel; set => SetField(ref shortcutLabel, value); }

    public static List<Transform> Defaults() => new()
    {
        new() { Name = "Mais formal", Prompt = "Reescreva em tom formal e profissional." },
        new() { Name = "Mais casual", Prompt = "Reescreva em tom casual e amigável, como uma conversa." },
        new() { Name = "Corrigir", Prompt = "Corrija ortografia, gramática e pontuação sem mudar o estilo." },
        new() { Name = "Resumir", Prompt = "Resuma em poucas frases, mantendo o essencial." },
        new() { Name = "Tópicos", Prompt = "Transforme em uma lista de tópicos clara e objetiva." },
        new() { Name = "Email", Prompt = "Transforme em um email bem estruturado, com saudação e despedida." },
        new() { Name = "Inglês", Prompt = "Traduza para o inglês, mantendo o tom." },
        new() { Name = "Mais curto", Prompt = "Deixe o texto mais curto e direto, sem perder informação importante." },
    };
}

/// Applies the user's dictionary replacements and snippets to a transcription.
public static class TextProcessor
{
    public static string Apply(string text, IEnumerable<Replacement> replacements, IEnumerable<Snippet> snippets)
    {
        var output = text;
        foreach (var replacement in replacements.Where(r => r.From.Length > 0))
        {
            output = ReplaceWords(replacement.From, replacement.To, output);
        }

        // A dictation that is only the trigger phrase becomes the whole expansion.
        var spoken = Normalize(output);
        var whole = snippets.FirstOrDefault(s => s.Trigger.Length > 0 && Normalize(s.Trigger) == spoken);
        if (whole != null) return whole.Expansion;

        foreach (var snippet in snippets.Where(s => s.Trigger.Length > 0))
        {
            output = ReplaceWords(snippet.Trigger, snippet.Expansion, output);
        }
        return output;
    }

    /// Vocabulary hint for Whisper so it spells custom words correctly.
    public static string? WhisperPrompt(IEnumerable<string> words)
    {
        var list = words.Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
        return list.Count == 0 ? null : string.Join(", ", list) + ".";
    }

    private static string ReplaceWords(string phrase, string replacement, string text)
    {
        var pattern = @"\b" + Regex.Escape(phrase) + @"\b";
        return Regex.Replace(text, pattern, replacement.Replace("$", "$$"), RegexOptions.IgnoreCase);
    }

    /// Case- and accent-insensitive, letters and digits only.
    private static string Normalize(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
