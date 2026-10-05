using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MadModStudio.AI.Models;

/// <summary>
/// One editable capability rule. Rules describe model properties (tier/class, context, tool support, prices) — never
/// which model is "better" at a task; that is learned from Mad Mod Studio's own history.
/// </summary>
public sealed class ModelProfileRule
{
    public string Provider { get; set; } = "";
    /// <summary>Regex matched against the model id. First matching rule for the provider wins.</summary>
    public string Pattern { get; set; } = ".*";
    public bool Exclude { get; set; }
    public ModelTier? Tier { get; set; }
    public int? ContextTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public bool? SupportsTools { get; set; }
    public bool? Reasoning { get; set; }
    public bool? Multimodal { get; set; }
    public decimal? InputCostPerMTok { get; set; }
    public decimal? OutputCostPerMTok { get; set; }
    public bool? SupportsEffort { get; set; }
    public string? RefusalFallbackModel { get; set; }
    public string? Note { get; set; }
}

public sealed class ModelProfileFile
{
    public int Version { get; set; } = 1;
    public string? Comment { get; set; }
    public List<ModelProfileRule> Rules { get; set; } = new();
}

/// <summary>
/// Loads capability rules from <c>config/model-profiles.json</c> in the data folder (created from built-in defaults
/// on first run, then user-editable) so model knowledge can be updated without changing the application.
/// </summary>
public sealed class ModelProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private List<(ModelProfileRule Rule, Regex Regex)> _rules = new();

    public ModelProfileStore(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        _path = Path.Combine(configDirectory, "model-profiles.json");
        Reload();
    }

    public string FilePath => _path;
    public string? LoadError { get; private set; }

    public void Reload()
    {
        ModelProfileFile file;
        try
        {
            if (!File.Exists(_path)) File.WriteAllText(_path, JsonSerializer.Serialize(Defaults(), Options));
            file = JsonSerializer.Deserialize<ModelProfileFile>(File.ReadAllText(_path), Options) ?? Defaults();
            LoadError = null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LoadError = $"model-profiles.json could not be read ({ex.Message}); using built-in defaults.";
            file = Defaults();
        }
        _rules = file.Rules.Select(r => (r, new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))).ToList();
    }

    public ModelProfileRule? Match(string providerId, string modelId) =>
        _rules.FirstOrDefault(r => r.Rule.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase) && r.Regex.IsMatch(modelId)).Rule;

    public static ModelProfileFile Defaults() => new()
    {
        Comment = "Capability profiles for model discovery. Edit freely: rules describe properties, not task quality. First matching rule per provider wins. Costs are USD per million tokens; leave null when unknown.",
        Rules = new()
        {
            // Anthropic (published prices; tiers follow the vendor's model families).
            new() { Provider = "anthropic", Pattern = "^claude-fable-5-1", Tier = ModelTier.Frontier, ContextTokens = 1_000_000, MaxOutputTokens = 128_000, Reasoning = true, Multimodal = true, InputCostPerMTok = 10, OutputCostPerMTok = 50, SupportsEffort = true, RefusalFallbackModel = "claude-opus-4-8" },
            new() { Provider = "anthropic", Pattern = "^claude-opus-5-5", Tier = ModelTier.Frontier, ContextTokens = 1_000_000, MaxOutputTokens = 128_000, Reasoning = true, Multimodal = true, InputCostPerMTok = 4, OutputCostPerMTok = 20, SupportsEffort = true, RefusalFallbackModel = "claude-opus-4-8" },
            new() { Provider = "anthropic", Pattern = "^claude-opus-5($|-)", Tier = ModelTier.Frontier, ContextTokens = 1_000_000, MaxOutputTokens = 128_000, Reasoning = true, Multimodal = true, InputCostPerMTok = 5, OutputCostPerMTok = 25, SupportsEffort = true, RefusalFallbackModel = "claude-opus-4-8" },
            new() { Provider = "anthropic", Pattern = "^claude-opus-4-[678]", Tier = ModelTier.Frontier, ContextTokens = 1_000_000, MaxOutputTokens = 128_000, Reasoning = true, Multimodal = true, InputCostPerMTok = 5, OutputCostPerMTok = 25, SupportsEffort = true },
            new() { Provider = "anthropic", Pattern = "^claude-sonnet-5", Tier = ModelTier.Balanced, ContextTokens = 1_000_000, MaxOutputTokens = 128_000, Reasoning = true, Multimodal = true, InputCostPerMTok = 2, OutputCostPerMTok = 10, SupportsEffort = true },
            new() { Provider = "anthropic", Pattern = "^claude-sonnet-4-6", Tier = ModelTier.Balanced, ContextTokens = 1_000_000, MaxOutputTokens = 128_000, Reasoning = true, Multimodal = true, InputCostPerMTok = 3, OutputCostPerMTok = 15, SupportsEffort = true },
            new() { Provider = "anthropic", Pattern = "^claude-haiku-4-5", Tier = ModelTier.Fast, ContextTokens = 200_000, Multimodal = true, InputCostPerMTok = 1, OutputCostPerMTok = 5, SupportsEffort = false },
            new() { Provider = "anthropic", Pattern = ".*", SupportsTools = true },

            // OpenAI: exclude non-chat models; tiers only from explicit naming conventions. Prices: fill in when known.
            new() { Provider = "openai", Pattern = "(embedding|tts|whisper|dall-e|moderation|image|audio|realtime|transcribe|search|computer-use|babbage|davinci|sora)", Exclude = true },
            new() { Provider = "openai", Pattern = "(mini|nano)", Tier = ModelTier.Fast, SupportsTools = true },
            new() { Provider = "openai", Pattern = ".*", SupportsTools = true },

            // Google Gemini.
            new() { Provider = "google", Pattern = "(embedding|aqa|imagen|veo|tts|image|learnlm|gemma)", Exclude = true },
            new() { Provider = "google", Pattern = "flash", Tier = ModelTier.Fast, SupportsTools = true, Multimodal = true },
            new() { Provider = "google", Pattern = "pro", Tier = ModelTier.Frontier, SupportsTools = true, Multimodal = true },
            new() { Provider = "google", Pattern = ".*", SupportsTools = true },

            // xAI Grok.
            new() { Provider = "xai", Pattern = "(image|imagine|video)", Exclude = true },
            new() { Provider = "xai", Pattern = "(mini|fast)", Tier = ModelTier.Fast, SupportsTools = true },
            new() { Provider = "xai", Pattern = ".*", SupportsTools = true },

            // OpenAI-compatible custom endpoint (local models etc.): unknown capabilities until you describe them here.
            new() { Provider = "custom", Pattern = ".*", SupportsTools = true },
        },
    };
}
