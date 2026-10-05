using System.Text.Json;

namespace MadModStudio.AI;

/// <summary>A tool the model may call. Tools run locally inside Mad Mod Studio.</summary>
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

public enum AIEventKind { Info, Thinking, Text, ToolCall, ToolResult, Warning, Error }

public sealed record AIEvent(AIEventKind Kind, string Message, DateTimeOffset TimestampUtc)
{
    public static AIEvent Now(AIEventKind kind, string message) => new(kind, message, DateTimeOffset.UtcNow);
}

public sealed class AIRunRequest
{
    public required string SystemPrompt { get; init; }
    public required string UserMessage { get; init; }
    public int MaxToolTurns { get; init; } = 25;
    /// <summary>Overrides the configured model for this run.</summary>
    public string? Model { get; init; }
    /// <summary>low, medium, high, xhigh or max.</summary>
    public string Effort { get; init; } = "high";
    public int MaxTokens { get; init; } = 32000;
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
    /// <summary>True when the provider's safety system declined the request.</summary>
    public bool Refused { get; init; }
    public int ToolCallCount { get; init; }
    public AIUsage Usage { get; init; } = new();
    public string? ModelUsed { get; init; }
}

/// <summary>
/// Provider-neutral AI interface. A provider owns the request/tool-execution loop so it can keep its native message
/// format (e.g. signed thinking blocks) intact between turns. Implementations: Anthropic (now); OpenAI, Gemini later.
/// </summary>
public interface IAIProvider
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>True when credentials are available. Never throws.</summary>
    bool IsConfigured { get; }
    string DefaultModel { get; }
    Task<AIRunResult> RunAsync(AIRunRequest request, IAIToolExecutor tools, IProgress<AIEvent>? progress = null, CancellationToken ct = default);
}

/// <summary>Describes, before anything is sent, which local data an AI operation may transmit externally.</summary>
public sealed record AIEgressNotice(string ProviderName, string Operation, IReadOnlyList<string> DataCategories, string Destination);

/// <summary>UI hook: asked before an AI operation sends project/log data to an external provider.</summary>
public interface IAIConsentService
{
    Task<bool> ConfirmAsync(AIEgressNotice notice, CancellationToken ct = default);
}

/// <summary>Consent implementation for non-interactive use (CLI with an explicit --yes flag, tests).</summary>
public sealed class PreApprovedConsent : IAIConsentService
{
    public Task<bool> ConfirmAsync(AIEgressNotice notice, CancellationToken ct = default) => Task.FromResult(true);
}
