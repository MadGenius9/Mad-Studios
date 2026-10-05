using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using MadModStudio.AI.Secrets;
using MadModStudio.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MadModStudio.AI.Providers;

/// <summary>
/// Claude via the official Anthropic C# SDK. Runs a manual tool-use loop where every tool executes locally; only the
/// prompt and the tool results the model explicitly asks for are sent. Refusal fallbacks are enabled server-side.
/// </summary>
public sealed class AnthropicProvider : IAIProvider
{
    public const string SecretName = "anthropic.apikey";
    public const string DefaultModelId = "claude-opus-5-5";
    private const int MaxToolResultChars = 60_000;

    private readonly ISecretStore _secrets;
    private readonly ILogger<AnthropicProvider> _log;

    public AnthropicProvider(ISecretStore secrets, ILogger<AnthropicProvider>? log = null)
    {
        _secrets = secrets;
        _log = log ?? NullLogger<AnthropicProvider>.Instance;
    }

    public string Id => "anthropic";
    public string DisplayName => "Anthropic Claude";
    public string DefaultModel => DefaultModelId;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_secrets.Get(SecretName));

    public async Task<AIRunResult> RunAsync(AIRunRequest request, IAIToolExecutor tools, IProgress<AIEvent>? progress = null, CancellationToken ct = default)
    {
        var apiKey = _secrets.Get(SecretName);
        if (string.IsNullOrWhiteSpace(apiKey))
            return new AIRunResult { Error = "No Anthropic API key is configured. Add one in Settings → AI Provider." };

        var model = request.Model ?? DefaultModelId;
        var client = new AnthropicClient { ApiKey = apiKey };
        var toolDefs = tools.Tools.Select(ToBetaTool).ToList();
        var messages = new List<BetaMessageParam> { new() { Role = Role.User, Content = request.UserMessage } };
        var usage = new AIUsage();
        var toolCalls = 0;
        var finalText = new System.Text.StringBuilder();
        string? stop = null;

        for (var turn = 0; turn <= request.MaxToolTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            BetaMessage response;
            try
            {
                response = await client.Beta.Messages.Create(new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = request.MaxTokens,
                    System = request.SystemPrompt,
                    Messages = messages,
                    Tools = toolDefs,
                    OutputConfig = new BetaOutputConfig { Effort = request.Effort },
                    // Server-side refusal fallback: a declined request is re-served by a fallback model in the same call.
                    Betas = ["server-side-fallback-2026-06-01"],
                    Fallbacks = new BetaFallbacksParam(new List<BetaFallbackParam> { new(Anthropic.Models.Messages.Model.ClaudeOpus4_8) }),
                }, ct).ConfigureAwait(false);
            }
            catch (AnthropicUnauthorizedException)
            {
                return Fail("The Anthropic API key was rejected (401). Check the key in Settings.", usage, toolCalls, model);
            }
            catch (AnthropicRateLimitException)
            {
                return Fail("Anthropic rate limit reached (429). Wait a moment and try again.", usage, toolCalls, model);
            }
            catch (AnthropicBadRequestException ex)
            {
                return Fail($"The AI request was rejected (400): {SecretRedactor.Redact(ex.Message)}", usage, toolCalls, model);
            }
            catch (Anthropic5xxException ex)
            {
                return Fail($"Anthropic service error: {SecretRedactor.Redact(ex.Message)}. Try again later.", usage, toolCalls, model);
            }
            catch (AnthropicIOException ex)
            {
                return Fail($"Could not reach the Anthropic API (offline or blocked?): {SecretRedactor.Redact(ex.Message)}", usage, toolCalls, model);
            }
            catch (AnthropicApiException ex)
            {
                return Fail($"Anthropic API error: {SecretRedactor.Redact(ex.Message)}", usage, toolCalls, model);
            }
            catch (HttpRequestException ex)
            {
                return Fail($"Network error contacting Anthropic: {SecretRedactor.Redact(ex.Message)}", usage, toolCalls, model);
            }

            usage.InputTokens += response.Usage.InputTokens;
            usage.OutputTokens += response.Usage.OutputTokens;
            usage.CacheReadTokens += response.Usage.CacheReadInputTokens ?? 0;
            stop = response.StopReason?.ToString();

            var stopRaw = StopReasonString(response);
            if (stopRaw == "refusal")
            {
                var category = response.StopDetails?.Category?.ToString();
                progress?.Report(AIEvent.Now(AIEventKind.Warning, $"The model declined this request{(category != null ? $" ({category})" : "")}."));
                return new AIRunResult { Refused = true, StopReason = stopRaw, Error = "The AI declined this request.", Usage = usage, ToolCallCount = toolCalls, ModelUsed = model };
            }

            var assistant = new List<BetaContentBlockParam>();
            var results = new List<BetaContentBlockParam>();
            // After a mid-output refusal fallback, model-internal blocks before the last fallback marker belong to the
            // declined attempt and must not be echoed; text blocks echo normally and the marker itself is dropped.
            var lastFallback = -1;
            for (var i = 0; i < response.Content.Count; i++)
                if (response.Content[i].TryPickFallback(out _)) lastFallback = i;
            if (lastFallback >= 0) progress?.Report(AIEvent.Now(AIEventKind.Info, "The request was re-served by the fallback model."));
            for (var i = 0; i < response.Content.Count; i++)
            {
                var block = response.Content[i];
                var beforeBoundary = i < lastFallback;
                if (block.TryPickFallback(out _)) continue;
                if (beforeBoundary && !block.TryPickText(out _)) continue;
                if (block.TryPickText(out var text))
                {
                    assistant.Add(new BetaTextBlockParam { Text = text.Text });
                    finalText.AppendLine(text.Text);
                    progress?.Report(AIEvent.Now(AIEventKind.Text, text.Text));
                }
                else if (block.TryPickThinking(out var thinking))
                {
                    // Echo unchanged (signature included) so the conversation stays valid.
                    assistant.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    if (!string.IsNullOrWhiteSpace(thinking.Thinking)) progress?.Report(AIEvent.Now(AIEventKind.Thinking, thinking.Thinking));
                }
                else if (block.TryPickRedactedThinking(out var redacted))
                {
                    assistant.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out var toolUse))
                {
                    assistant.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                    toolCalls++;
                    var input = JsonSerializer.SerializeToElement(toolUse.Input);
                    progress?.Report(AIEvent.Now(AIEventKind.ToolCall, $"{toolUse.Name}({Abbrev(input.GetRawText(), 300)})"));
                    AIToolResult result;
                    try { result = await tools.ExecuteAsync(toolUse.Name, input, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result = AIToolResult.Error($"Tool failed: {ex.Message}"); }
                    var content = result.Content.Length > MaxToolResultChars
                        ? result.Content[..MaxToolResultChars] + $"\n…(truncated {result.Content.Length - MaxToolResultChars} characters; request a narrower range)"
                        : result.Content;
                    progress?.Report(AIEvent.Now(result.IsError ? AIEventKind.Warning : AIEventKind.ToolResult, $"{toolUse.Name} → {Abbrev(content, 200)}"));
                    results.Add(new BetaToolResultBlockParam { ToolUseID = toolUse.ID, Content = content, IsError = result.IsError });
                }
            }

            if (stopRaw == "tool_use" && results.Count > 0)
            {
                messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = assistant });
                // All tool results for one assistant turn go back in a single user message.
                messages.Add(new BetaMessageParam { Role = Role.User, Content = results });
                continue;
            }
            if (stopRaw == "pause_turn")
            {
                messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = assistant });
                continue;
            }
            if (stopRaw == "max_tokens")
            {
                progress?.Report(AIEvent.Now(AIEventKind.Warning, "The response hit the output token limit and may be incomplete."));
                return new AIRunResult { Success = false, FinalText = finalText.ToString(), StopReason = stopRaw, Error = "Response was cut off at the token limit.", Usage = usage, ToolCallCount = toolCalls, ModelUsed = model };
            }
            return new AIRunResult { Success = true, FinalText = finalText.ToString().Trim(), StopReason = stopRaw, Usage = usage, ToolCallCount = toolCalls, ModelUsed = model };
        }
        return new AIRunResult { Success = false, FinalText = finalText.ToString(), StopReason = stop, Error = $"Stopped after {request.MaxToolTurns} tool turns.", Usage = usage, ToolCallCount = toolCalls, ModelUsed = model };
    }

    private static string? StopReasonString(BetaMessage m)
    {
        return m.StopReason?.Raw();
    }

    private static AIRunResult Fail(string error, AIUsage usage, int toolCalls, string model) =>
        new() { Success = false, Error = error, Usage = usage, ToolCallCount = toolCalls, ModelUsed = model };

    private static BetaToolUnion ToBetaTool(AIToolDefinition def)
    {
        var props = new Dictionary<string, JsonElement>();
        if (def.InputSchema.TryGetProperty("properties", out var p))
            foreach (var prop in p.EnumerateObject()) props[prop.Name] = prop.Value.Clone();
        var required = def.InputSchema.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(x => x.GetString()!).ToList()
            : new List<string>();
        return new BetaTool
        {
            Name = def.Name,
            Description = def.Description,
            InputSchema = new() { Properties = props, Required = required },
        };
    }

    private static string Abbrev(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
