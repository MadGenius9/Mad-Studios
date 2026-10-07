using System.Text.Json;
using MadModStudio.AI.Models;

namespace MadModStudio.AI;

/// <summary>A tool the model may call. Tools always run locally inside Mad Mod Studio.</summary>
public sealed record AIToolDefinition(string Name, string Description, JsonElement InputSchema);

public sealed record AIToolResult(string Content, bool IsError = false)
{
    public static AIToolResult Error(string message) => new(message, true);
}

public interface IAIToolExecutor
{
    IReadOnlyList<AIToolDefinition> Tools { get; }
    Task<AIToolResult> ExecuteAsync(string toolName, JsonElement input, CancellationToken ct);
}

/// <summary>RequestStarted/ResponseReceived bracket each model call so the UI can show real waiting time and token counts.</summary>
public enum AIEventKind { Info, Thinking, Text, ToolCall, ToolResult, Warning, Error, RequestStarted, ResponseReceived }

public sealed record AIEvent(AIEventKind Kind, string Message, DateTimeOffset TimestampUtc)
{
    public static AIEvent Now(AIEventKind kind, string message) => new(kind, message, DateTimeOffset.UtcNow);
}

public sealed class AIRunRequest
{
    public required string SystemPrompt { get; init; }
    public required string UserMessage { get; init; }
    /// <summary>Provider model id chosen by the router (or by the user).</summary>
    public required string Model { get; init; }
    public int MaxToolTurns { get; init; } = 25;
    /// <summary>low, medium, high, xhigh or max — applied only where the model profile says it is supported.</summary>
    public string ReasoningEffort { get; init; } = "high";
    public int MaxTokens { get; init; } = 32000;
    /// <summary>Telemetry label (e.g. the agent kind). Never sent to the provider.</summary>
    public string? Tag { get; init; }
}

public sealed class AIUsage
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
}

public sealed class AIRunResult
{
    public bool Success { get; init; }
    public string FinalText { get; init; } = "";
    public string? StopReason { get; init; }
    public string? Error { get; init; }
    public bool Refused { get; init; }
    public int ToolCallCount { get; init; }
    public AIUsage Usage { get; init; } = new();
    public string? ModelUsed { get; init; }
}

public enum ConnectionState { NotConfigured, Unverified, Connected, Failed }

public sealed record ProviderConnectionStatus(ConnectionState State, string Message, DateTimeOffset? CheckedUtc)
{
    public static readonly ProviderConnectionStatus NotConfigured = new(ConnectionState.NotConfigured, "Not configured", null);
}

/// <summary>
/// One AI service (Anthropic, OpenAI, Google Gemini, xAI, an OpenAI-compatible endpoint...). A provider owns the
/// request/tool loop so its native message format stays intact. Nothing else in Mad Mod Studio depends on a specific
/// provider.
/// </summary>
public interface IAIProvider
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>Where data is sent (shown to the user before any request).</summary>
    string Destination { get; }
    /// <summary>True when credentials (or an endpoint) are available. Never makes a network call.</summary>
    bool IsConfigured { get; }
    /// <summary>Result of the last real connection test in this session.</summary>
    ProviderConnectionStatus Status { get; }
    /// <summary>Makes a real, cheap API call (model listing) to verify the credentials.</summary>
    Task<ProviderConnectionStatus> TestConnectionAsync(CancellationToken ct = default);
    /// <summary>Models reported by the provider's API (no profile merging).</summary>
    Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken ct = default);
    Task<AIRunResult> RunAsync(AIRunRequest request, IAIToolExecutor tools, IProgress<AIEvent>? progress = null, CancellationToken ct = default);
}

/// <summary>Describes, before anything is sent, which local data an AI operation may transmit, and to which providers.</summary>
public sealed record AIEgressNotice(string ProviderName, string Operation, IReadOnlyList<string> DataCategories, string Destination);

public interface IAIConsentService
{
    Task<bool> ConfirmAsync(AIEgressNotice notice, CancellationToken ct = default);
}

public sealed class PreApprovedConsent : IAIConsentService
{
    public Task<bool> ConfirmAsync(AIEgressNotice notice, CancellationToken ct = default) => Task.FromResult(true);
}
