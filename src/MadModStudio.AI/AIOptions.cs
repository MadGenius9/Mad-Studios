using MadModStudio.AI.Providers;
using MadModStudio.Core.Abstractions;

namespace MadModStudio.AI;

/// <summary>User-adjustable AI settings, persisted in the application settings table.</summary>
public sealed class AIOptions
{
    public const string ModelKey = "ai.model";
    public const string MaxAttemptsKey = "ai.maxRepairAttempts";
    public const int DefaultMaxAttempts = 3;

    public string Model { get; set; } = AnthropicProvider.DefaultModelId;
    public int MaxAutoRepairAttempts { get; set; } = DefaultMaxAttempts;

    public async Task LoadAsync(ISettingsRepository settings)
    {
        Model = await settings.GetAsync(ModelKey) is { Length: > 0 } m ? m : AnthropicProvider.DefaultModelId;
        MaxAutoRepairAttempts = int.TryParse(await settings.GetAsync(MaxAttemptsKey), out var n) && n is >= 1 and <= 10 ? n : DefaultMaxAttempts;
    }

    public async Task SaveAsync(ISettingsRepository settings)
    {
        await settings.SetAsync(ModelKey, Model);
        await settings.SetAsync(MaxAttemptsKey, MaxAutoRepairAttempts.ToString());
    }
}
