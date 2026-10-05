using System.Text.Json;
using System.Text.Json.Serialization;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.Knowledge;

namespace MadModStudio.AI;

/// <summary>Global AI settings: defaults, routing, control level, privacy, limits and preferences. Persisted as JSON.</summary>
public sealed class AIPolicy
{
    public const string SettingsKey = "ai.policy";
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string? DefaultProvider { get; set; }
    /// <summary>"provider/model" key.</summary>
    public string? DefaultModel { get; set; }
    public RoutingMode RoutingMode { get; set; } = RoutingMode.Auto;
    public AgentControlLevel ControlLevel { get; set; } = AgentControlLevel.Guided;

    // Privacy
    public bool AllowCrossProviderRouting { get; set; }
    public bool AllowSourceToExternal { get; set; } = true;
    public bool AllowLogsToExternal { get; set; } = true;
    public bool AskBeforeEachRun { get; set; } = true;

    // Limits
    public int MaxConcurrentAgents { get; set; } = 3;
    public int MaxRepairAttempts { get; set; } = 3;
    public int MaxModelsPerTask { get; set; } = 3;
    public bool AutoEscalate { get; set; }
    public int EscalationFailureThreshold { get; set; } = 2;
    public decimal? SessionBudgetUsd { get; set; }
    public decimal? ProjectBudgetUsd { get; set; }
    public decimal? TaskBudgetUsd { get; set; }

    /// <summary>Task type → "provider/model" lock. Missing = AUTO.</summary>
    public Dictionary<AITaskType, string> TaskPreferences { get; set; } = new();

    // OpenAI-compatible custom endpoint (local models, gateways)
    public string? CustomEndpointUrl { get; set; }
    public string CustomEndpointName { get; set; } = "Custom (OpenAI-compatible)";

    public static async Task<AIPolicy> LoadAsync(ISettingsRepository settings)
    {
        var json = await settings.GetAsync(SettingsKey);
        if (string.IsNullOrWhiteSpace(json)) return new AIPolicy();
        try { return JsonSerializer.Deserialize<AIPolicy>(json, Json) ?? new AIPolicy(); }
        catch (JsonException) { return new AIPolicy(); }
    }

    public async Task SaveAsync(ISettingsRepository settings)
    {
        Normalize();
        await settings.SetAsync(SettingsKey, JsonSerializer.Serialize(this, Json));
    }

    public void CopyFrom(AIPolicy other)
    {
        foreach (var p in typeof(AIPolicy).GetProperties().Where(p => p.CanWrite))
            p.SetValue(this, p.GetValue(other));
    }

    public void Normalize()
    {
        MaxConcurrentAgents = Math.Clamp(MaxConcurrentAgents, 1, 8);
        MaxRepairAttempts = Math.Clamp(MaxRepairAttempts, 1, 10);
        MaxModelsPerTask = Math.Clamp(MaxModelsPerTask, 1, 6);
        EscalationFailureThreshold = Math.Clamp(EscalationFailureThreshold, 1, 5);
    }
}
