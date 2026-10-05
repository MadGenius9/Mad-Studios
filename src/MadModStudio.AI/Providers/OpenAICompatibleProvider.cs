using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MadModStudio.AI.Models;
using MadModStudio.AI.Secrets;
using MadModStudio.Core.Security;

namespace MadModStudio.AI.Providers;

/// <summary>
/// Chat Completions–style provider (function calling) over HTTP. Used for OpenAI, xAI and any OpenAI-compatible
/// endpoint (gateways, local model servers). Each provider is configured by id, base URL and credential name.
/// </summary>
public class OpenAICompatibleProvider : IAIProvider
{
    private readonly ISecretStore _secrets;
    private readonly Func<string?> _baseUrl;
    private readonly HttpClient _http;
    private readonly bool _keyRequired;
    private readonly string _maxTokensField;

    public OpenAICompatibleProvider(string id, string displayName, Func<string?> baseUrl, ISecretStore secrets, bool keyRequired = true,
        string maxTokensField = "max_completion_tokens", HttpClient? http = null)
    {
        Id = id;
        DisplayName = displayName;
        _baseUrl = baseUrl;
        _secrets = secrets;
        _keyRequired = keyRequired;
        _maxTokensField = maxTokensField;
        _http = http ?? ProviderSupport.SharedHttp;
    }

    public string Id { get; }
    public virtual string DisplayName { get; }
    public string Destination => _baseUrl() is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : "(no endpoint configured)";
    public ProviderConnectionStatus Status { get; private set; } = ProviderConnectionStatus.NotConfigured;

    private string? Key => _secrets.Get(SecretNames.ApiKey(Id));

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_baseUrl()) && (!_keyRequired || !string.IsNullOrWhiteSpace(Key));

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var baseUrl = _baseUrl()!.TrimEnd('/');
        var req = new HttpRequestMessage(method, baseUrl + path);
        if (Key is { Length: > 0 } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return req;
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
        using var req = NewRequest(HttpMethod.Get, "/models");
        var json = await SendAsync(req, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var list = new List<ProviderModel>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                int? ctx = m.TryGetProperty("context_length", out var c) && c.TryGetInt32(out var cv) ? cv
                    : m.TryGetProperty("context_window", out var c2) && c2.TryGetInt32(out var cv2) ? cv2 : null;
                list.Add(new ProviderModel(Id, id!, id, ctx));
            }
        }
        return list;
    }

    private async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new ProviderException($"Could not reach {DisplayName} ({Destination}): {SecretRedactor.Redact(ex.Message)}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException($"{DisplayName} did not respond in time."); }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new ProviderException(ProviderSupport.DescribeHttpError(DisplayName, resp.StatusCode, body));
            return body;
        }
    }

    public async Task<AIRunResult> RunAsync(AIRunRequest request, IAIToolExecutor tools, IProgress<AIEvent>? progress = null, CancellationToken ct = default)
    {
        if (!IsConfigured) return new AIRunResult { Error = $"{DisplayName} is not configured. Add it in Settings → AI Providers.", ModelUsed = request.Model };
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = request.UserMessage },
        };
        var toolDefs = new JsonArray();
        foreach (var t in tools.Tools)
            toolDefs.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.InputSchema.GetRawText()) },
            });

        var usage = new AIUsage();
        var toolCalls = 0;
        var finalText = new StringBuilder();
        for (var turn = 0; turn <= request.MaxToolTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages.DeepClone(), [_maxTokensField] = request.MaxTokens };
            if (toolDefs.Count > 0) { body["tools"] = toolDefs.DeepClone(); body["tool_choice"] = "auto"; }
            using var req = NewRequest(HttpMethod.Post, "/chat/completions");
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            string json;
            try { json = await SendAsync(req, ct).ConfigureAwait(false); }
            catch (ProviderException ex) { return new AIRunResult { Error = ex.Message, Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model }; }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var u))
            {
                if (u.TryGetProperty("prompt_tokens", out var pt)) usage.InputTokens += pt.GetInt64();
                if (u.TryGetProperty("completion_tokens", out var cpt)) usage.OutputTokens += cpt.GetInt64();
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return new AIRunResult { Error = $"{DisplayName} returned no choices.", Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };
            var choice = choices[0];
            var finish = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;
            var message = choice.GetProperty("message");
            var text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                finalText.AppendLine(text);
                progress?.Report(AIEvent.Now(AIEventKind.Text, text!));
            }
            if (finish == "content_filter")
                return new AIRunResult { Refused = true, StopReason = finish, Error = $"{DisplayName} declined this request (content filter).", Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };

            var calls = message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array ? tc : default;
            if (calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0)
            {
                // Echo the assistant message exactly as returned, then one tool message per call.
                messages.Add(JsonNode.Parse(message.GetRawText()));
                foreach (var call in calls.EnumerateArray())
                {
                    var id = call.GetProperty("id").GetString() ?? "";
                    var fn = call.GetProperty("function");
                    var name = fn.GetProperty("name").GetString() ?? "";
                    var argsText = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                    toolCalls++;
                    AIToolResult result;
                    JsonElement args;
                    try
                    {
                        using var argDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsText) ? "{}" : argsText);
                        args = argDoc.RootElement.Clone();
                        progress?.Report(AIEvent.Now(AIEventKind.ToolCall, $"{name}({ProviderSupport.Abbrev(argsText, 300)})"));
                        result = await ProviderSupport.ExecuteToolSafeAsync(tools, name, args, ct).ConfigureAwait(false);
                    }
                    catch (JsonException) { result = AIToolResult.Error("Tool arguments were not valid JSON. Call the tool again with valid JSON arguments."); }
                    var content = ProviderSupport.LimitToolResult(result.Content);
                    progress?.Report(AIEvent.Now(result.IsError ? AIEventKind.Warning : AIEventKind.ToolResult, $"{name} → {ProviderSupport.Abbrev(content, 200)}"));
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result.IsError ? "ERROR: " + content : content });
                }
                continue;
            }
            if (finish == "length")
                return new AIRunResult { Success = false, FinalText = finalText.ToString(), StopReason = finish, Error = "Response was cut off at the token limit.", Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };
            return new AIRunResult { Success = true, FinalText = finalText.ToString().Trim(), StopReason = finish, Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };
        }
        return new AIRunResult { Error = $"Stopped after {request.MaxToolTurns} tool turns.", FinalText = finalText.ToString(), Usage = usage, ToolCallCount = toolCalls, ModelUsed = request.Model };
    }
}

public sealed class ProviderException : Exception
{
    public ProviderException(string message) : base(message) { }
}
