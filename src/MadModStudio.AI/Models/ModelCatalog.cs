using System.Text.Json;
using MadModStudio.AI.Providers;

namespace MadModStudio.AI.Models;

public interface IAIProviderRegistry
{
    IReadOnlyList<IAIProvider> All { get; }
    IAIProvider? Get(string providerId);
}

public sealed class AIProviderRegistry : IAIProviderRegistry
{
    private readonly List<IAIProvider> _providers;
    public AIProviderRegistry(IEnumerable<IAIProvider> providers) => _providers = providers.ToList();
    public IReadOnlyList<IAIProvider> All => _providers;
    public IAIProvider? Get(string providerId) => _providers.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
}

public sealed record CatalogRefreshResult(string ProviderId, bool Success, int ModelCount, string? Error);

public interface IModelCatalog
{
    /// <summary>Known models of configured providers (from the last successful discovery, merged with capability profiles).</summary>
    IReadOnlyList<ModelInfo> Models { get; }
    ModelInfo? Find(string providerId, string modelId);
    ModelInfo? Find(string? key);
    /// <summary>Queries each configured provider's model API. Providers that fail keep their last known list.</summary>
    Task<IReadOnlyList<CatalogRefreshResult>> RefreshAsync(CancellationToken ct = default);
    event EventHandler? Changed;
}

/// <summary>
/// Model discovery. Model lists come from the providers' APIs where available, are merged with the editable capability
/// profiles, and are cached on disk so the UI can show the last known models offline (marked as not verified).
/// </summary>
public sealed class ModelCatalog : IModelCatalog
{
    private readonly IAIProviderRegistry _providers;
    private readonly ModelProfileStore _profiles;
    private readonly string _cachePath;
    private readonly object _gate = new();
    private Dictionary<string, List<ProviderModel>> _raw = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _liveProviders = new(StringComparer.OrdinalIgnoreCase);
    private List<ModelInfo> _models = new();

    public ModelCatalog(IAIProviderRegistry providers, ModelProfileStore profiles, string configDirectory)
    {
        _providers = providers;
        _profiles = profiles;
        _cachePath = Path.Combine(configDirectory, "models-cache.json");
        LoadCache();
        Rebuild();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ModelInfo> Models { get { lock (_gate) return _models; } }

    public ModelInfo? Find(string providerId, string modelId) =>
        Models.FirstOrDefault(m => m.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) && m.ModelId == modelId);

    public ModelInfo? Find(string? key) => ModelKey.Parse(key) is { } k ? Find(k.Provider, k.Model) : null;

    public async Task<IReadOnlyList<CatalogRefreshResult>> RefreshAsync(CancellationToken ct = default)
    {
        var results = new List<CatalogRefreshResult>();
        var tasks = _providers.All.Where(p => p.IsConfigured).Select(async p =>
        {
            try
            {
                var models = await p.ListModelsAsync(ct).ConfigureAwait(false);
                lock (_gate)
                {
                    _raw[p.Id] = models.ToList();
                    _liveProviders.Add(p.Id);
                }
                return new CatalogRefreshResult(p.Id, true, models.Count, null);
            }
            catch (ProviderException ex) { return new CatalogRefreshResult(p.Id, false, 0, ex.Message); }
        }).ToList();
        results.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        SaveCache();
        Rebuild();
        return results;
    }

    /// <summary>Rebuilds merged metadata (e.g. after editing model-profiles.json or changing credentials).</summary>
    public void Rebuild()
    {
        var list = new List<ModelInfo>();
        lock (_gate)
        {
            foreach (var (providerId, models) in _raw)
            {
                var provider = _providers.Get(providerId);
                if (provider is null) continue;
                foreach (var m in models)
                {
                    var rule = _profiles.Match(providerId, m.ModelId);
                    if (rule?.Exclude == true) continue;
                    list.Add(new ModelInfo
                    {
                        ProviderId = providerId,
                        ProviderName = provider.DisplayName,
                        ModelId = m.ModelId,
                        DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? m.ModelId : m.DisplayName!,
                        ContextTokens = m.ContextTokens ?? rule?.ContextTokens,
                        MaxOutputTokens = m.MaxOutputTokens ?? rule?.MaxOutputTokens,
                        SupportsTools = m.SupportsTools ?? rule?.SupportsTools ?? true,
                        Reasoning = m.Reasoning ?? rule?.Reasoning ?? false,
                        Multimodal = m.Multimodal ?? rule?.Multimodal ?? false,
                        Tier = rule?.Tier ?? ModelTier.Unknown,
                        InputCostPerMTok = rule?.InputCostPerMTok,
                        OutputCostPerMTok = rule?.OutputCostPerMTok,
                        SupportsEffort = rule?.SupportsEffort ?? false,
                        RefusalFallbackModel = rule?.RefusalFallbackModel,
                        // A model is available when its provider is configured; "verified" only when listed live this session.
                        Available = provider.IsConfigured,
                        MetadataSource = (_liveProviders.Contains(providerId) ? "Provider API" : "Cached provider list (not refreshed this session)")
                            + (rule != null && rule.Pattern != ".*" ? " + capability profile" : ""),
                    });
                }
            }
            _models = list.OrderBy(m => m.ProviderName).ThenBy(m => m.DisplayName).ToList();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
                _raw = JsonSerializer.Deserialize<Dictionary<string, List<ProviderModel>>>(File.ReadAllText(_cachePath)) is { } d
                    ? new Dictionary<string, List<ProviderModel>>(d, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { _raw = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void SaveCache()
    {
        try
        {
            Dictionary<string, List<ProviderModel>> copy;
            lock (_gate) copy = new(_raw);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(copy));
        }
        catch (IOException) { }
    }
}
