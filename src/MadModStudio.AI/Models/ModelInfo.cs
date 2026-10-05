namespace MadModStudio.AI.Models;

/// <summary>A model as reported by a provider's API.</summary>
public sealed record ProviderModel(string ProviderId, string ModelId, string? DisplayName = null, int? ContextTokens = null, int? MaxOutputTokens = null,
    bool? SupportsTools = null, bool? Reasoning = null, bool? Multimodal = null);

public enum ModelTier { Unknown, Fast, Balanced, Frontier }

/// <summary>Merged model metadata (provider API + editable capability profile). Never a claim about task quality.</summary>
public sealed record ModelInfo
{
    public string ProviderId { get; init; } = "";
    public string ProviderName { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int? ContextTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool SupportsTools { get; init; } = true;
    public bool SupportsStreaming { get; init; } = true;
    public bool Multimodal { get; init; }
    public bool Reasoning { get; init; }
    public bool Available { get; init; }
    public ModelTier Tier { get; init; }
    public decimal? InputCostPerMTok { get; init; }
    public decimal? OutputCostPerMTok { get; init; }
    public bool SupportsEffort { get; init; }
    public string? RefusalFallbackModel { get; init; }
    /// <summary>Where the metadata came from ("Provider API", "Capability profile", ...).</summary>
    public string MetadataSource { get; init; } = "";

    public string Key => ModelKey.Of(ProviderId, ModelId);
    public bool CostKnown => InputCostPerMTok != null && OutputCostPerMTok != null;

    public decimal? EstimateCost(long inputTokens, long outputTokens) => CostKnown
        ? inputTokens / 1_000_000m * InputCostPerMTok!.Value + outputTokens / 1_000_000m * OutputCostPerMTok!.Value
        : null;

    public override string ToString() => $"{ProviderName} / {DisplayName}";
}

public static class ModelKey
{
    public static string Of(string providerId, string modelId) => $"{providerId}/{modelId}";

    public static (string Provider, string Model)? Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var i = key.IndexOf('/');
        return i <= 0 || i == key.Length - 1 ? null : (key[..i], key[(i + 1)..]);
    }
}
