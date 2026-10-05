using System.Net;
using System.Text.Json;
using MadModStudio.Core.Security;

namespace MadModStudio.AI.Providers;

public static class ProviderIds
{
    public const string Anthropic = "anthropic";
    public const string OpenAI = "openai";
    public const string Google = "google";
    public const string XAI = "xai";
    public const string Custom = "custom";
}

public static class SecretNames
{
    public static string ApiKey(string providerId) => providerId + ".apikey";
}

/// <summary>Shared helpers for provider implementations (HTTP client, error mapping, tool-result limits).</summary>
public static class ProviderSupport
{
    public const int MaxToolResultChars = 60_000;

    /// <summary>One HttpClient for all providers. Credentials are only ever set per request, never on the client.</summary>
    public static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string LimitToolResult(string content) => content.Length > MaxToolResultChars
        ? content[..MaxToolResultChars] + $"\n…(truncated {content.Length - MaxToolResultChars} characters; request a narrower range)"
        : content;

    public static string Abbrev(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Turns an HTTP error into an actionable, secret-free message.</summary>
    public static string DescribeHttpError(string provider, HttpStatusCode status, string body)
    {
        var detail = ExtractErrorMessage(body);
        var msg = (int)status switch
        {
            401 or 403 => $"{provider} rejected the API key ({(int)status}). Check it in Settings → AI Providers.",
            404 => $"{provider} returned 404 — the model or endpoint was not found.",
            429 => $"{provider} rate limit or quota reached (429). Wait and retry, or choose another model.",
            >= 500 => $"{provider} service error ({(int)status}). Try again later or choose another model.",
            _ => $"{provider} rejected the request ({(int)status}).",
        };
        return SecretRedactor.Redact(string.IsNullOrWhiteSpace(detail) ? msg : $"{msg} {detail}");
    }

    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return Abbrev(e.GetString() ?? "", 400);
                if (e.TryGetProperty("message", out var m)) return Abbrev(m.GetString() ?? "", 400);
            }
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(body) ? null : Abbrev(body, 200);
    }

    public static async Task<AIToolResult> ExecuteToolSafeAsync(IAIToolExecutor tools, string name, JsonElement input, CancellationToken ct)
    {
        try { return await tools.ExecuteAsync(name, input, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return AIToolResult.Error($"Tool failed: {ex.Message}"); }
    }
}
