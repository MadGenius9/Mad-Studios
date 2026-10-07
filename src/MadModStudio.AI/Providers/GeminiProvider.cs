using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MadModStudio.AI.Models;
using MadModStudio.AI.Secrets;
using MadModStudio.Core.Security;

namespace MadModStudio.AI.Providers;

/// <summary>Google Gemini via the Generative Language REST API (generateContent with function calling).</summary>
public sealed class GeminiProvider : IAIProvider
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";
    private readonly ISecretStore _secrets;
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public GeminiProvider(ISecretStore secrets, HttpClient? http = null, string baseUrl = DefaultBaseUrl)
    {
        _secrets = secrets;
        _http = http ?? ProviderSupport.SharedHttp;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public string Id => ProviderIds.Google;
    public string DisplayName => "Google Gemini";
    public string Destination => new Uri(_baseUrl).Host;
    public ProviderConnectionStatus Status { get; private set; } = ProviderConnectionStatus.NotConfigured;
    private string? Key => _secrets.Get(SecretNames.ApiKey(Id));
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key);

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        req.Headers.Add("x-goog-api-key", Key);
        return req;
    }

    private async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new ProviderException($"Could not reach Google Gemini: {SecretRedactor.Redact(ex.Message)}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException("Google Gemini did not respond in time."); }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new ProviderException(ProviderSupport.DescribeHttpError(DisplayName, resp.StatusCode, body));
            return body;
        }
    }

    public async Task<ProviderConnectionStatus> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return Status = ProviderConnectionStatus.NotConfigured;
        try
        {
            var models = await ListModelsAsync(ct).ConfigureAwait(false);
            Status = new ProviderConnectionStatus(ConnectionState.Connected, $"Connected — {models.Count} model(s) listed", DateTimeOffset.UtcNow);
        }
        catch (ProviderException ex) { Status = new ProviderConnectionStatus(ConnectionState.Failed, ex.Message, DateTimeOffset.UtcNow); }
        return Status;
    }

    public async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<ProviderModel>();
        var list = new List<ProviderModel>();
        string? token = null;
        for (var page = 0; page < 10; page++)
        {
            using var req = NewRequest(HttpMethod.Get, "/models?pageSize=1000" + (token is null ? "" : "&pageToken=" + Uri.EscapeDataString(token)));
            using var doc = JsonDocument.Parse(await SendAsync(req, ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var m in models.EnumerateArray())
                {
                    var methods = m.TryGetProperty("supportedGenerationMethods", out var sm) ? sm.EnumerateArray().Select(x => x.GetString()).ToList() : new List<string?>();
                    if (!methods.Contains("generateContent")) continue;
                    var name = m.GetProperty("name").GetString() ?? "";
                    var id = name.StartsWith("models/") ? name[7..] : name;
                    list.Add(new ProviderModel(Id, id,
                        m.TryGetProperty("displayName", out var dn) ? dn.GetString() : id,
                        m.TryGetProperty("inputTokenLimit", out var il) && il.TryGetInt32(out var ilv) ? ilv : null,
                        m.TryGetProperty("outputTokenLimit", out var ol) && ol.TryGetInt32(out var olv) ? olv : null,
                        SupportsTools: true,
                        Reasoning: m.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.True ? true : null));
                }
            }
            token = doc.RootElement.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(token)) break;
        }
        return list;
    }

    /// <summary>Gemini's schema dialect uses upper-case types and doesn't accept some JSON Schema keywords.</summary>
    public static JsonNode? ConvertSchema(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (k, v) in obj)
                {
                    if (k is "additionalProperties" or "$schema") continue;
                    if (k == "type" && v is JsonValue tv && tv.TryGetValue<string>(out var t)) result[k] = t.ToUpperInvariant();
                    else result[k] = ConvertSchema(v);
                }
                return result;
            case JsonArray arr:
                var a = new JsonArray();
                foreach (var item in arr) a.Add(ConvertSchema(item));
                return a;
            default:
                return node?.DeepClone();
        }
    }

    public async Task<AIRunResult> RunAsync(AIRunRequest request, IAIToolExecutor tools, IProgress<AIEvent>? progress = null, CancellationToken ct = default)
    {
        if (!IsConfigured) return new AIRunResult { Error = "Google Gemini is not configured. Add a key in Settings → AI Providers.", ModelUsed = request.Model };
        var contents = new JsonArray { new JsonObject { ["role"] = "user", ["parts"] = new JsonArray { new JsonObject { ["text"] = request.UserMessage } } } };
        var decls = new JsonArray();
        foreach (var t in tools.Tools)
            decls.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = ConvertSchema(JsonNode.Parse(t.InputSchema.GetRawText())) });

        var usage = new AIUsage();
        var toolCalls = 0;
        var finalText = new StringBuilder();
        for (var turn = 0; turn <= request.MaxToolTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            var body = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = request.SystemPrompt } } },
                ["contents"] = contents.DeepClone(),
                ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = request.MaxTokens },
            };
            if (decls.Count > 0) body["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = decls.DeepClone() } };
            using var req = NewRequest(HttpMethod.Post, $"/models/{Uri.EscapeDataString(request.Model)}:generateContent");
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            string json;
            progress?.Report(AIEvent.Now(AIEventKind.RequestStarted, $"Waiting for {request.Model} (turn {turn + 1})"));
            try { json = await SendAsync(req, ct).ConfigureAwait(false); }
            catch (ProviderException ex) { return new AIRunResult { Error = ex.Message, Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model }; }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            long turnIn = 0, turnOut = 0;
            if (root.TryGetProperty("usageMetadata", out var um))
            {
                if (um.TryGetProperty("promptTokenCount", out var p)) turnIn = p.GetInt64();
                if (um.TryGetProperty("candidatesTokenCount", out var cand)) turnOut = cand.GetInt64();
            }
            usage.InputTokens += turnIn;
            usage.OutputTokens += turnOut;
            progress?.Report(AIEvent.Now(AIEventKind.ResponseReceived, $"{request.Model} replied ({turnIn:N0} input / {turnOut:N0} output tokens)"));
            if (root.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out var br))
                return new AIRunResult { Refused = true, StopReason = br.GetString(), Error = $"Gemini blocked this request ({br.GetString()}).", Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };
            if (!root.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0)
                return new AIRunResult { Error = "Gemini returned no candidates.", Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };
            var candidate = cands[0];
            var finish = candidate.TryGetProperty("finishReason", out var f) ? f.GetString() : null;
            if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII")
                return new AIRunResult { Refused = true, StopReason = finish, Error = $"Gemini declined this request ({finish}).", Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };
            if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
                return new AIRunResult { Success = finish == "STOP", FinalText = finalText.ToString().Trim(), StopReason = finish, Error = finish == "STOP" ? null : $"Gemini stopped ({finish}).", Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };

            // Echo the model turn unchanged (it may carry thought signatures that must round-trip).
            contents.Add(JsonNode.Parse(content.GetRawText()));
            var responses = new JsonArray();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var tx) && !(part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True))
                {
                    finalText.AppendLine(tx.GetString());
                    progress?.Report(AIEvent.Now(AIEventKind.Text, tx.GetString() ?? ""));
                }
                if (part.TryGetProperty("functionCall", out var fc))
                {
                    var name = fc.GetProperty("name").GetString() ?? "";
                    var args = fc.TryGetProperty("args", out var a) ? a.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                    toolCalls++;
                    progress?.Report(AIEvent.Now(AIEventKind.ToolCall, $"{name}({ProviderSupport.Abbrev(args.GetRawText(), 300)})"));
                    var result = await ProviderSupport.ExecuteToolSafeAsync(tools, name, args, ct).ConfigureAwait(false);
                    var text = ProviderSupport.LimitToolResult(result.Content);
                    progress?.Report(AIEvent.Now(result.IsError ? AIEventKind.Warning : AIEventKind.ToolResult, $"{name} → {ProviderSupport.Abbrev(text, 200)}"));
                    var fr = new JsonObject { ["name"] = name, ["response"] = new JsonObject { [result.IsError ? "error" : "result"] = text } };
                    if (fc.TryGetProperty("id", out var fid)) fr["id"] = fid.GetString();
                    responses.Add(new JsonObject { ["functionResponse"] = fr });
                }
            }
            if (responses.Count > 0)
            {
                contents.Add(new JsonObject { ["role"] = "user", ["parts"] = responses });
                continue;
            }
            if (finish == "MAX_TOKENS")
                return new AIRunResult { FinalText = finalText.ToString(), StopReason = finish, Error = "Response was cut off at the token limit.", Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };
            return new AIRunResult { Success = true, FinalText = finalText.ToString().Trim(), StopReason = finish, Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };
        }
        return new AIRunResult { Error = $"Stopped after {request.MaxToolTurns} tool turns.", FinalText = finalText.ToString(), Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };
    }
}
