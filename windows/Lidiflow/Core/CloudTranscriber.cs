using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Lidiflow.Core;

/// Cloud transcription through an OpenAI-compatible audio API (OpenAI or Groq), using the user's own key.
public static class CloudTranscriber
{
    public static readonly LlmProvider[] Providers = { LlmProvider.OpenAI, LlmProvider.Groq };

    public static string[] Models(LlmProvider provider) => provider switch
    {
        LlmProvider.Groq => new[] { "whisper-large-v3-turbo", "whisper-large-v3" },
        _ => new[] { "gpt-4o-transcribe", "gpt-4o-mini-transcribe", "whisper-1" },
    };

    public static async Task<string> TranscribeAsync(byte[] wav, string language, string? prompt)
    {
        var settings = AppSettings.Current;
        var provider = settings.TranscriptionProvider;
        var key = Credentials.Get(provider)
            ?? throw new UserFacingException($"Adicione sua chave da {provider.Title()} em Modelos");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(settings.TranscriptionModel(provider)), "model");
        form.Add(new StringContent("json"), "response_format");
        if (language != "auto") form.Add(new StringContent(language), "language");
        if (prompt != null) form.Add(new StringContent(prompt), "prompt");
        form.Add(WavContent(wav), "file", "audio.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl() + "/audio/transcriptions") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var deadline = Http.Deadline(60);
        using var response = await Http.Client.SendAsync(request, deadline.Token);
        var json = JsonHelpers.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        var text = JsonHelpers.String(() => json?["text"]);
        if (text != null) return text.Trim();

        var message = JsonHelpers.String(() => json?["error"]?["message"]);
        throw new UserFacingException($"{provider.Title()}: {message ?? "resposta inválida"}");
    }

    public static ByteArrayContent WavContent(byte[] wav)
    {
        var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return content;
    }
}

public static class JsonHelpers
{
    public static JsonNode? Parse(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch
        {
            return null;
        }
    }

    /// Reads a string out of an untrusted JSON shape; any mismatch is just null.
    public static string? String(System.Func<JsonNode?> path)
    {
        try
        {
            return path() is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        }
        catch
        {
            return null;
        }
    }
}
