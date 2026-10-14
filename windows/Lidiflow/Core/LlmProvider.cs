using System;

namespace Lidiflow.Core;

/// Where polish, command mode and transforms run.
public enum LlmProvider { Ollama, OpenAI, Anthropic, Groq, Gemini, Xai }

public static class LlmProviders
{
    public static readonly LlmProvider[] Cloud =
        { LlmProvider.OpenAI, LlmProvider.Anthropic, LlmProvider.Groq, LlmProvider.Gemini, LlmProvider.Xai };

    public static string Title(this LlmProvider provider) => provider switch
    {
        LlmProvider.Ollama => "Ollama (local)",
        LlmProvider.OpenAI => "OpenAI",
        LlmProvider.Anthropic => "Anthropic (Claude)",
        LlmProvider.Groq => "Groq",
        LlmProvider.Gemini => "Google Gemini",
        LlmProvider.Xai => "xAI (Grok)",
        _ => provider.ToString(),
    };

    public static bool NeedsKey(this LlmProvider provider) => provider != LlmProvider.Ollama;

    /// Base URL of the OpenAI-compatible API. Anthropic uses its own Messages API.
    public static string BaseUrl(this LlmProvider provider) => provider switch
    {
        LlmProvider.Ollama => AppSettings.Current.OllamaBaseUrl.TrimEnd('/'),
        LlmProvider.OpenAI => "https://api.openai.com/v1",
        LlmProvider.Anthropic => "https://api.anthropic.com/v1",
        LlmProvider.Groq => "https://api.groq.com/openai/v1",
        LlmProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai",
        LlmProvider.Xai => "https://api.x.ai/v1",
        _ => "",
    };

    /// Starting suggestions; "Atualizar lista" fetches the provider's live catalog.
    public static string[] SuggestedModels(this LlmProvider provider) => provider switch
    {
        LlmProvider.Ollama => Array.ConvertAll(OllamaManager.Catalog, model => model.Id),
        LlmProvider.OpenAI => new[] { "gpt-4.1-mini", "gpt-4.1", "gpt-4o-mini" },
        LlmProvider.Anthropic => new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" },
        LlmProvider.Groq => new[] { "llama-3.3-70b-versatile", "llama-3.1-8b-instant" },
        LlmProvider.Gemini => new[] { "gemini-2.5-flash", "gemini-2.5-pro" },
        LlmProvider.Xai => new[] { "grok-3-mini", "grok-3" },
        _ => Array.Empty<string>(),
    };

    public static string? KeyUrl(this LlmProvider provider) => provider switch
    {
        LlmProvider.OpenAI => "https://platform.openai.com/api-keys",
        LlmProvider.Anthropic => "https://console.anthropic.com/settings/keys",
        LlmProvider.Groq => "https://console.groq.com/keys",
        LlmProvider.Gemini => "https://aistudio.google.com/apikey",
        LlmProvider.Xai => "https://console.x.ai",
        _ => null,
    };
}

/// A provider in a ComboBox, shown by its title.
public sealed record ProviderItem(LlmProvider Provider)
{
    public override string ToString() => Provider.Title();
}
