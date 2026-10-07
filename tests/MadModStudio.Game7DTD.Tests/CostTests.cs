using MadModStudio.AI.Models;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

public class ModelPriceTests
{
    [Fact]
    public void Setting_a_price_keeps_the_models_other_properties_and_only_affects_that_model()
    {
        var store = new ModelProfileStore(FakeGame.TempDir("cfg"));
        Assert.Equal(ModelTier.Fast, store.Match("openai", "gpt-x-mini")!.Tier);
        Assert.Null(store.Match("openai", "gpt-x-mini")!.InputCostPerMTok);

        store.SetPrice("openai", "gpt-x-mini", 0.4m, 1.6m);
        var rule = store.Match("openai", "gpt-x-mini")!;
        Assert.Equal(ModelTier.Fast, rule.Tier); // copied from the rule that matched before
        Assert.Equal(0.4m, rule.InputCostPerMTok);
        Assert.Equal(1.6m, rule.OutputCostPerMTok);
        Assert.Null(store.Match("openai", "gpt-x-mini-2")!.InputCostPerMTok); // exact match only
        Assert.Null(store.Match("openai", "gpt-x")!.InputCostPerMTok);

        // Updating reuses the same rule; reloading from disk keeps it.
        store.SetPrice("openai", "gpt-x-mini", 0.5m, 2m);
        var reloaded = new ModelProfileStore(Path.GetDirectoryName(store.FilePath)!);
        Assert.Equal(0.5m, reloaded.Match("openai", "gpt-x-mini")!.InputCostPerMTok);
        Assert.Single(File.ReadAllLines(store.FilePath), l => l.Contains("^gpt-x-mini$"));

        store.SetPrice("openai", "gpt-x-mini", null, null);
        Assert.Null(store.Match("openai", "gpt-x-mini")!.InputCostPerMTok);
    }

    [Fact]
    public void Excluded_models_cannot_be_priced_and_negative_prices_are_rejected()
    {
        var store = new ModelProfileStore(FakeGame.TempDir("cfg"));
        Assert.Throws<InvalidOperationException>(() => store.SetPrice("openai", "text-embedding-3-large", 1m, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetPrice("openai", "gpt-x", -1m, 1m));
    }
}
