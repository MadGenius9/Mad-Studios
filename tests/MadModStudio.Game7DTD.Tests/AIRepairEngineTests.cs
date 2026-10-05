using System.Text.Json;
using MadModStudio.AI;
using MadModStudio.AI.Engines;
using MadModStudio.AI.Secrets;
using MadModStudio.AI.Providers;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

public class AIRepairEngineTests
{
    /// <summary>Scripted provider: drives the real toolbox exactly like a model would, without any network access.</summary>
    private sealed class ScriptedProvider : IAIProvider
    {
        private readonly Func<IAIToolExecutor, int, Task> _script;
        public int Calls;
        public ScriptedProvider(Func<IAIToolExecutor, int, Task> script) => _script = script;
        public string Id => "scripted";
        public string DisplayName => "Scripted";
        public bool IsConfigured { get; init; } = true;
        public string DefaultModel => "test";
        public async Task<AIRunResult> RunAsync(AIRunRequest request, IAIToolExecutor tools, IProgress<AIEvent>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            await _script(tools, Calls);
            return new AIRunResult { Success = true, FinalText = "done" };
        }
    }

    private static async Task<(TestHost Host, ModProject Project)> BrokenProject()
    {
        var host = new TestHost();
        var profiles = host.Get<GameProfileService>();
        var profile = (await profiles.CreateProfileAsync(FakeGame.Shared)).Profile!;
        await profiles.ReindexAsync(profile);
        var broken = SampleMod.PatchSource.Replace("__instance.IsHome", "__instance.IsAtHomeBase");
        var zip = SampleMod.WriteZip(FakeGame.TempDir("inbox"), patchSource: broken);
        var project = (await host.Get<ProjectService>().ImportAsync(zip, profile.Id)).Single().Project;
        return (host, project);
    }

    private static AIRepairEngine Engine(TestHost host, IAIProvider provider) =>
        new(provider, new PreApprovedConsent(), host.Get<ModBuildPipeline>(), host.Get<ProjectService>(), host.Get<GameProfileService>());

    [Fact]
    public async Task Repairs_compile_error_using_game_api_lookup_and_records_revisions()
    {
        var (host, project) = await BrokenProject();
        using var _ = host;
        string? lookedUp = null;
        var provider = new ScriptedProvider(async (tools, call) =>
        {
            var diag = await tools.ExecuteAsync("get_compiler_diagnostics", JsonSerializer.SerializeToElement(new { }), default);
            Assert.Contains("IsAtHomeBase", diag.Content);
            lookedUp = (await tools.ExecuteAsync("get_game_type", JsonSerializer.SerializeToElement(new { type_name = "EntityDrone" }), default)).Content;
            var file = await tools.ExecuteAsync("read_project_file", JsonSerializer.SerializeToElement(new { path = "Harmony/Patches.cs" }), default);
            Assert.Contains("10: ", file.Content);
            var fixedSource = SampleMod.PatchSource;
            var r = await tools.ExecuteAsync("propose_file_changes", JsonSerializer.SerializeToElement(new
            {
                summary = "EntityDrone has IsHome, not IsAtHomeBase (verified via get_game_type).",
                files = new[] { new { path = "Harmony/Patches.cs", content = fixedSource } },
            }), default);
            Assert.False(r.IsError, r.Content);
        });

        var outcome = await Engine(host, provider).RepairAsync(project, new RepairRequestOptions());

        Assert.True(outcome.Succeeded, outcome.StopReason);
        var attempt = Assert.Single(outcome.Attempts);
        Assert.True(attempt.CompileSucceededAfter);
        Assert.Contains("public bool IsHome { get; set; }", lookedUp);
        var history = await host.Get<ProjectService>().History.ListAsync(project);
        Assert.Contains(history, r => r.Action == "Before: AI repair attempt 1");
        var applied = history.First(r => r.Action == "AI repair attempt 1");
        Assert.Contains("M Harmony/Patches.cs", applied.ChangedFiles);
        Assert.Contains("verified via get_game_type", applied.Reason);
    }

    [Fact]
    public async Task Stops_after_max_attempts_when_fixes_do_not_work()
    {
        var (host, project) = await BrokenProject();
        using var _ = host;
        var provider = new ScriptedProvider(async (tools, call) =>
        {
            var bad = SampleMod.PatchSource.Replace("__instance.IsHome", $"__instance.StillWrong{call}");
            await tools.ExecuteAsync("propose_file_changes", JsonSerializer.SerializeToElement(new
            {
                summary = $"Attempt {call}",
                files = new[] { new { path = "Harmony/Patches.cs", content = bad } },
            }), default);
        });

        var outcome = await Engine(host, provider).RepairAsync(project, new RepairRequestOptions { MaxAttempts = 3 });

        Assert.False(outcome.Succeeded);
        Assert.Equal(3, outcome.Attempts.Count);
        Assert.Equal(3, provider.Calls);
        Assert.Contains("Stopped after 3", outcome.StopReason);
        Assert.False(outcome.FinalBuild!.CompileSucceeded);
    }

    [Fact]
    public async Task Unsafe_paths_are_rejected_and_unconfigured_provider_is_reported()
    {
        var (host, project) = await BrokenProject();
        using var _ = host;
        AIToolResult? result = null;
        var provider = new ScriptedProvider(async (tools, call) =>
        {
            result = await tools.ExecuteAsync("propose_file_changes", JsonSerializer.SerializeToElement(new
            {
                summary = "evil",
                files = new[] { new { path = "../../outside.txt", content = "x" } },
            }), default);
        });
        var outcome = await Engine(host, provider).RepairAsync(project, new RepairRequestOptions { MaxAttempts = 1 });
        Assert.True(result!.IsError);
        Assert.Contains("Unsafe path", result.Content);
        Assert.Contains("did not propose", outcome.StopReason);

        var off = await Engine(host, new ScriptedProvider((_, _) => Task.CompletedTask) { IsConfigured = false }).RepairAsync(project, new RepairRequestOptions());
        Assert.Contains("not configured", off.StopReason);
        Assert.Empty(off.Attempts);
    }

    [Fact]
    public async Task Anthropic_provider_without_key_fails_gracefully()
    {
        var provider = new AnthropicProvider(new CompositeSecretStore(new EnvironmentSecretStore(new Dictionary<string, string> { ["anthropic.apikey"] = "MMS_TEST_NO_SUCH_VAR" })));
        Assert.False(provider.IsConfigured);
        var r = await provider.RunAsync(new AIRunRequest { SystemPrompt = "s", UserMessage = "u" }, new ScriptedTools());
        Assert.False(r.Success);
        Assert.Contains("No Anthropic API key", r.Error);
    }

    private sealed class ScriptedTools : IAIToolExecutor
    {
        public IReadOnlyList<AIToolDefinition> Tools => Array.Empty<AIToolDefinition>();
        public Task<AIToolResult> ExecuteAsync(string toolName, JsonElement input, CancellationToken ct) => Task.FromResult(new AIToolResult(""));
    }
}
