using System.Net;
using System.Text;
using System.Text.Json;
using MadModStudio.AI;
using MadModStudio.AI.Agents;
using MadModStudio.AI.Coordination;
using MadModStudio.AI.Knowledge;
using MadModStudio.AI.Models;
using MadModStudio.AI.Providers;
using MadModStudio.AI.Routing;
using MadModStudio.AI.Secrets;
using MadModStudio.Core.Knowledge;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.TestSupport;
using static MadModStudio.Game7DTD.Tests.ScriptedProvider;

namespace MadModStudio.Game7DTD.Tests;

public class ModelRouterTests
{
    private sealed class FakeCatalog : IModelCatalog
    {
        public FakeCatalog(params ModelInfo[] models) => Models = models;
        public IReadOnlyList<ModelInfo> Models { get; }
        public ModelInfo? Find(string providerId, string modelId) => Models.FirstOrDefault(m => m.ProviderId == providerId && m.ModelId == modelId);
        public ModelInfo? Find(string? key) => Models.FirstOrDefault(m => m.Key == key);
        public Task<IReadOnlyList<CatalogRefreshResult>> RefreshAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CatalogRefreshResult>>(Array.Empty<CatalogRefreshResult>());
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class FakeTracker : IModelPerformanceTracker
    {
        public List<ModelPerformanceRecord> Records { get; } = new();
        public Task<ModelPerformanceRecord> StartAsync(string provider, string model, AgentKind agent, AITaskType taskType, Guid? projectId, string? taskId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteAsync(ModelPerformanceRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ModelTaskStats>> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(ModelPerformanceTracker.Aggregate(Records));
        public Task<IReadOnlyList<ModelPerformanceRecord>> GetRecordsAsync(Guid? projectId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelPerformanceRecord>>(Records);
    }

    private static ModelInfo M(string provider, string id, ModelTier tier, decimal? cin = null, decimal? cout = null, bool tools = true, int? ctx = null) => new()
    {
        ProviderId = provider, ProviderName = provider, ModelId = id, DisplayName = id, Tier = tier, Available = true, SupportsTools = tools,
        InputCostPerMTok = cin, OutputCostPerMTok = cout, ContextTokens = ctx,
    };

    private static (ModelRouter Router, AIPolicy Policy, FakeTracker Tracker) Make(params ModelInfo[] models)
    {
        var providers = new AIProviderRegistry(models.Select(m => m.ProviderId).Distinct().Select(p => (IAIProvider)new ScriptedProvider(p)));
        var policy = new AIPolicy { AllowCrossProviderRouting = true };
        var tracker = new FakeTracker();
        return (new ModelRouter(new FakeCatalog(models), providers, tracker, policy), policy, tracker);
    }

    private static readonly ModelInfo Big = M("p1", "big", ModelTier.Frontier, 10, 50);
    private static readonly ModelInfo Small = M("p2", "small", ModelTier.Fast, 1, 5);
    private static readonly ModelInfo Mid = M("p3", "mid", ModelTier.Balanced, 2, 10);

    [Theory]
    [InlineData(RoutingMode.BestQuality, "big")]
    [InlineData(RoutingMode.Fast, "small")]
    [InlineData(RoutingMode.Economy, "small")]
    [InlineData(RoutingMode.Auto, "big")]
    public async Task Modes_choose_by_capability_profile_without_history(RoutingMode mode, string expected)
    {
        var (router, _, _) = Make(Big, Small, Mid);
        var d = await router.SelectAsync(new RoutingRequest { TaskType = AITaskType.HarmonyRepair, Mode = mode });
        Assert.Equal(expected, d.Model!.ModelId);
        Assert.Contains(d.Reasons, r => r.Contains(mode.ToString()));
    }

    [Fact]
    public async Task Recorded_history_outweighs_tier_and_is_explained()
    {
        var (router, _, tracker) = Make(Big, Mid);
        for (var i = 0; i < 10; i++)
        {
            tracker.Records.Add(new ModelPerformanceRecord { Provider = "p3", Model = "mid", TaskType = AITaskType.HarmonyRepair, Outcome = i < 9 ? TaskOutcome.Success : TaskOutcome.Failure, ValidationPassed = i < 8 });
            tracker.Records.Add(new ModelPerformanceRecord { Provider = "p1", Model = "big", TaskType = AITaskType.HarmonyRepair, Outcome = i < 2 ? TaskOutcome.Success : TaskOutcome.Failure });
        }
        var d = await router.SelectAsync(new RoutingRequest { TaskType = AITaskType.HarmonyRepair, Mode = RoutingMode.Auto });
        Assert.Equal("mid", d.Model!.ModelId);
        Assert.Contains(d.Reasons, r => r.Contains("9 of 10 similar HarmonyRepair tasks succeeded") && r.Contains("8 of 10 passed validation"));

        // Different task type: no history there, so capability decides again.
        var xml = await router.SelectAsync(new RoutingRequest { TaskType = AITaskType.XmlRepair, Mode = RoutingMode.Auto });
        Assert.Equal("big", xml.Model!.ModelId);
    }

    [Fact]
    public async Task Too_little_history_is_not_used()
    {
        var (router, _, tracker) = Make(Big, Mid);
        tracker.Records.Add(new ModelPerformanceRecord { Provider = "p3", Model = "mid", TaskType = AITaskType.Review, Outcome = TaskOutcome.Success });
        var d = await router.SelectAsync(new RoutingRequest { TaskType = AITaskType.Review });
        Assert.Equal("big", d.Model!.ModelId);
        Assert.Equal(1, (await tracker.GetStatsAsync()).Single().Total);
        Assert.False((await tracker.GetStatsAsync()).Single().HasEnoughData);
    }

    [Fact]
    public async Task Preferences_manual_choices_exclusions_and_requirements()
    {
        var (router, policy, _) = Make(Big, Small, Mid, M("p4", "notools", ModelTier.Frontier, tools: false), M("p5", "tiny", ModelTier.Frontier, ctx: 8000));
        policy.TaskPreferences[AITaskType.Architecture] = "p2/small";
        Assert.Equal("small", (await router.SelectAsync(new RoutingRequest { TaskType = AITaskType.Architecture, Mode = RoutingMode.BestQuality })).Model!.ModelId);
        Assert.Equal("mid", (await router.SelectAsync(new RoutingRequest { TaskType = AITaskType.Review, ManualModel = "p3/mid" })).Model!.ModelId);
        var excluded = await router.SelectAsync(new RoutingRequest { TaskType = AITaskType.Review, Mode = RoutingMode.BestQuality, ExcludedModels = new HashSet<string> { "p1/big", "p5/tiny" }, EstimatedContextTokens = 20000 });
        Assert.NotEqual("notools", excluded.Model!.ModelId);
        Assert.Equal("mid", excluded.Model.ModelId);
        var unavailable = await router.SelectAsync(new RoutingRequest { ManualModel = "p9/ghost" });
        Assert.Null(unavailable.Model);
        Assert.Contains("not available", unavailable.Error);
    }

    [Fact]
    public async Task Cross_provider_off_restricts_automatic_routing()
    {
        var (router, policy, _) = Make(Big, Small);
        policy.AllowCrossProviderRouting = false;
        policy.DefaultProvider = "p2";
        Assert.Equal("small", (await router.SelectAsync(new RoutingRequest { Mode = RoutingMode.BestQuality })).Model!.ModelId);
        Assert.Equal("big", (await router.SelectAsync(new RoutingRequest { Mode = RoutingMode.BestQuality, ForSuggestionOnly = true, ExcludedModels = new HashSet<string> { "p2/small" } })).Model!.ModelId);
    }

    [Fact]
    public async Task Independent_review_prefers_a_different_model()
    {
        var (router, _, _) = Make(Big, Mid);
        var d = await router.SelectAsync(new RoutingRequest { TaskType = AITaskType.Review, Mode = RoutingMode.BestQuality, PreferDifferentFrom = new HashSet<string> { "p1/big" } });
        Assert.Equal("mid", d.Model!.ModelId);
        Assert.Contains(d.Reasons, r => r.Contains("Independent"));
    }
}

public class ProviderWireTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Code, string Body)> _responses;
        public FakeHandler(params (HttpStatusCode, string)[] responses) => _responses = new(responses);
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var (code, body) = _responses.Dequeue();
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _d = new();
        public string? Get(string name) => _d.GetValueOrDefault(name);
        public void Set(string name, string value) => _d[name] = value;
        public void Delete(string name) => _d.Remove(name);
        public bool CanWrite => true;
        public string Description => "memory";
    }

    private sealed class EchoTools : IAIToolExecutor
    {
        public List<(string Name, string Args)> Calls { get; } = new();
        public IReadOnlyList<AIToolDefinition> Tools { get; } = new[]
        {
            new AIToolDefinition("verify_api", "Verify", JsonSerializer.SerializeToElement(new { type = "object", properties = new { type_name = new { type = "string" } }, required = new[] { "type_name" }, additionalProperties = false })),
        };
        public Task<AIToolResult> ExecuteAsync(string toolName, JsonElement input, CancellationToken ct)
        {
            Calls.Add((toolName, input.GetRawText()));
            return Task.FromResult(new AIToolResult("EntityDrone exists"));
        }
    }

    [Fact]
    public async Task OpenAI_compatible_tool_loop_round_trips()
    {
        var handler = new FakeHandler(
            (HttpStatusCode.OK, """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"verify_api","arguments":"{\"type_name\":\"EntityDrone\"}"}}]}}],"usage":{"prompt_tokens":100,"completion_tokens":20}}"""),
            (HttpStatusCode.OK, """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"EntityDrone is verified."}}],"usage":{"prompt_tokens":150,"completion_tokens":10}}"""));
        var secrets = new MemorySecrets();
        secrets.Set(SecretNames.ApiKey("xai"), "sk-test-secret-12345678");
        var provider = new OpenAICompatibleProvider("xai", "xAI Grok", () => "https://api.x.ai/v1", secrets, maxTokensField: "max_tokens", http: new HttpClient(handler));
        var tools = new EchoTools();

        var r = await provider.RunAsync(new AIRunRequest { SystemPrompt = "sys", UserMessage = "hi", Model = "grok-x" }, tools);

        Assert.True(r.Success, r.Error);
        Assert.Equal("EntityDrone is verified.", r.FinalText);
        Assert.Equal(250, r.Usage.InputTokens);
        Assert.Equal(("verify_api", """{"type_name":"EntityDrone"}"""), tools.Calls.Single());
        var first = handler.Seen[0];
        Assert.Equal("https://api.x.ai/v1/chat/completions", first.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", first.Request.Headers.Authorization!.Scheme);
        using var body1 = JsonDocument.Parse(first.Body);
        Assert.Equal("grok-x", body1.RootElement.GetProperty("model").GetString());
        Assert.Equal("verify_api", body1.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.True(body1.RootElement.TryGetProperty("max_tokens", out _));
        using var body2 = JsonDocument.Parse(handler.Seen[1].Body);
        var msgs = body2.RootElement.GetProperty("messages");
        Assert.Equal("call_1", msgs[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("tool", msgs[3].GetProperty("role").GetString());
        Assert.Equal("call_1", msgs[3].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task Rejected_key_is_reported_without_leaking_it()
    {
        var handler = new FakeHandler((HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided: sk-test-secret-12345678"}}"""));
        var secrets = new MemorySecrets();
        secrets.Set(SecretNames.ApiKey("openai"), "sk-test-secret-12345678");
        var provider = new OpenAICompatibleProvider("openai", "OpenAI GPT", () => "https://api.openai.com/v1", secrets, http: new HttpClient(handler));

        var status = await provider.TestConnectionAsync();

        Assert.Equal(ConnectionState.Failed, status.State);
        Assert.Contains("rejected the API key", status.Message);
        Assert.DoesNotContain("sk-test-secret", status.Message);
    }

    [Fact]
    public async Task Unconfigured_providers_never_report_connected()
    {
        var provider = new OpenAICompatibleProvider("openai", "OpenAI GPT", () => "https://api.openai.com/v1", new MemorySecrets(), http: new HttpClient(new FakeHandler()));
        Assert.False(provider.IsConfigured);
        Assert.Equal(ConnectionState.NotConfigured, (await provider.TestConnectionAsync()).State);
        Assert.Contains("not configured", (await provider.RunAsync(new AIRunRequest { SystemPrompt = "", UserMessage = "", Model = "x" }, new EchoTools())).Error);
    }

    [Fact]
    public async Task Gemini_function_calling_round_trips_and_converts_schema()
    {
        var handler = new FakeHandler(
            (HttpStatusCode.OK, """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"verify_api","args":{"type_name":"EntityDrone"}},"thoughtSignature":"sig123"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":80,"candidatesTokenCount":5}}"""),
            (HttpStatusCode.OK, """{"candidates":[{"content":{"role":"model","parts":[{"text":"Done."}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":90,"candidatesTokenCount":3}}"""));
        var secrets = new MemorySecrets();
        secrets.Set(SecretNames.ApiKey("google"), "AIzaTestKey1234567890123456");
        var provider = new GeminiProvider(secrets, new HttpClient(handler));
        var tools = new EchoTools();

        var r = await provider.RunAsync(new AIRunRequest { SystemPrompt = "sys", UserMessage = "hi", Model = "gemini-x-pro" }, tools);

        Assert.True(r.Success, r.Error);
        Assert.Equal("Done.", r.FinalText);
        Assert.Single(tools.Calls);
        var first = handler.Seen[0];
        Assert.EndsWith("/models/gemini-x-pro:generateContent", first.Request.RequestUri!.AbsolutePath);
        Assert.True(first.Request.Headers.Contains("x-goog-api-key"));
        Assert.DoesNotContain("AIza", first.Request.RequestUri.ToString());
        using var b1 = JsonDocument.Parse(first.Body);
        var parameters = b1.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parameters");
        Assert.Equal("OBJECT", parameters.GetProperty("type").GetString());
        Assert.False(parameters.TryGetProperty("additionalProperties", out _));
        using var b2 = JsonDocument.Parse(handler.Seen[1].Body);
        var contents = b2.RootElement.GetProperty("contents");
        Assert.Equal("sig123", contents[1].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString()); // echoed unchanged
        Assert.Equal("EntityDrone exists", contents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("response").GetProperty("result").GetString());
    }

    [Fact]
    public async Task Gemini_model_listing_keeps_only_generate_content_models()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, """{"models":[{"name":"models/gemini-x-flash","displayName":"Gemini X Flash","inputTokenLimit":1048576,"outputTokenLimit":65536,"supportedGenerationMethods":["generateContent","countTokens"]},{"name":"models/text-embedding-x","supportedGenerationMethods":["embedContent"]}]}"""));
        var secrets = new MemorySecrets();
        secrets.Set(SecretNames.ApiKey("google"), "k");
        var models = await new GeminiProvider(secrets, new HttpClient(handler)).ListModelsAsync();
        var m = Assert.Single(models);
        Assert.Equal("gemini-x-flash", m.ModelId);
        Assert.Equal(1048576, m.ContextTokens);
    }
}

public class ModelCatalogTests
{
    [Fact]
    public async Task Discovery_merges_profiles_filters_non_chat_models_and_caches()
    {
        var dir = FakeGame.TempDir("catalog");
        var openai = new ScriptedProvider("openai", "text-embedding-3-small", "gpt-x-mini", "gpt-x");
        var profiles = new ModelProfileStore(dir);
        var catalog = new ModelCatalog(new AIProviderRegistry(new IAIProvider[] { openai }), profiles, dir);
        Assert.Empty(catalog.Models);

        var results = await catalog.RefreshAsync();

        Assert.True(Assert.Single(results).Success);
        Assert.Equal(new[] { "gpt-x", "gpt-x-mini" }, catalog.Models.Select(m => m.ModelId).OrderBy(x => x));
        Assert.Equal(ModelTier.Fast, catalog.Find("openai/gpt-x-mini")!.Tier);
        Assert.Equal(ModelTier.Unknown, catalog.Find("openai/gpt-x")!.Tier); // no assumption without a profile rule
        Assert.Null(catalog.Find("openai/gpt-x")!.InputCostPerMTok); // no invented prices
        Assert.Contains("Provider API", catalog.Find("openai/gpt-x")!.MetadataSource);

        var offline = new ModelCatalog(new AIProviderRegistry(new IAIProvider[] { openai }), profiles, dir);
        Assert.Equal(2, offline.Models.Count);
        Assert.Contains("Cached", offline.Models[0].MetadataSource);

        openai.IsConfigured = false;
        var unconfigured = new ModelCatalog(new AIProviderRegistry(new IAIProvider[] { openai }), profiles, dir);
        Assert.All(unconfigured.Models, m => Assert.False(m.Available));
    }

    [Fact]
    public void Anthropic_profile_supplies_published_metadata()
    {
        var store = new ModelProfileStore(FakeGame.TempDir("profiles"));
        var rule = store.Match("anthropic", "claude-opus-5-5")!;
        Assert.Equal(ModelTier.Frontier, rule.Tier);
        Assert.Equal(4m, rule.InputCostPerMTok);
        Assert.True(rule.SupportsEffort);
        Assert.False(store.Match("anthropic", "claude-haiku-4-5")!.SupportsEffort);
        Assert.True(store.Match("openai", "text-embedding-3-large")!.Exclude);
    }
}

public class OwnershipAndKnowledgeTests
{
    [Fact]
    public async Task One_writer_per_file_at_a_time()
    {
        var mgr = new FileOwnershipManager();
        var project = Guid.NewGuid();
        var first = await mgr.AcquireWriteAsync(project, new[] { "a.cs", "b.cs" }, "Harmony Agent");
        Assert.Equal("Harmony Agent", mgr.Owners(project)["a.cs"]);
        var second = mgr.AcquireWriteAsync(project, new[] { "b.cs" }, "XML Agent");
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        var other = await mgr.AcquireWriteAsync(project, new[] { "c.xml" }, "XML Agent"); // different file: not blocked
        await first.DisposeAsync();
        await using (await second) Assert.Equal("XML Agent", mgr.Owners(project)["b.cs"]);
        await other.DisposeAsync();
        Assert.Empty(mgr.Owners(project));
    }

    [Fact]
    public async Task Stale_reads_are_rejected_instead_of_overwriting_other_work()
    {
        using var host = new AITestHost(providers: new ScriptedProvider("alpha", "a1"));
        var (project, _) = await host.ImportBrokenAsync();
        var path = Path.Combine(project.SourcePath, "Harmony", "Patches.cs");
        var readHash = FileOwnershipManager.Hash(File.ReadAllText(path));
        var changes = host.Get<AgentChangeService>();
        var meta = new Dictionary<string, string> { ["agent"] = "test" };
        var reads = new Dictionary<string, string> { ["Harmony/Patches.cs"] = readHash };

        var a = await changes.ApplyAsync(project, new ProposedChanges("A", new[] { new FileEdit("Harmony/Patches.cs", "// A") }), reads, "A", meta, "A");
        var b = await changes.ApplyAsync(project, new ProposedChanges("B", new[] { new FileEdit("Harmony/Patches.cs", "// B") }), reads, "B", meta, "B");
        var blind = await changes.ApplyAsync(project, new ProposedChanges("C", new[] { new FileEdit("Harmony/ModInit.cs", "// C") }), new Dictionary<string, string>(), "C", meta, "C");

        Assert.True(a.Applied);
        Assert.False(b.Applied);
        Assert.Contains("changed by another agent", Assert.Single(b.Conflicts));
        Assert.False(blind.Applied);
        Assert.Contains("did not read", Assert.Single(blind.Conflicts));
        Assert.Equal("// A", File.ReadAllText(path));
        Assert.Equal("Harmony/Patches.cs", a.Before!.Metadata["filesAboutToChange"]);
    }

    [Fact]
    public async Task Game_update_marks_knowledge_stale_and_reverifies_structured_findings()
    {
        using var host = new AITestHost(providers: new ScriptedProvider("alpha", "a1"));
        var gameDir = Path.Combine(FakeGame.TempDir("updgame"), "7 Days To Die");
        FakeGame.Create(gameDir);
        var profiles = host.Get<GameProfileService>();
        var profile = (await profiles.CreateProfileAsync(gameDir)).Profile!;
        await profiles.ReindexAsync(profile);
        var zip = SampleMod.WriteZip(FakeGame.TempDir("inbox"));
        var project = (await host.Get<ProjectService>().ImportAsync(zip, profile.Id)).Single().Project;
        var knowledge = host.Get<ProjectKnowledgeService>();
        var sendHome = await knowledge.RecordApiClaimAsync(project, profile, "EntityDrone", "SendHome", null, "GameApiResearch", "alpha", "a1", null, null);
        var isHome = await knowledge.RecordApiClaimAsync(project, profile, "EntityDrone", "IsHome", null, "GameApiResearch", "alpha", "a1", null, null);
        var recall = await knowledge.RecordApiClaimAsync(project, profile, "EntityDrone", "Recall", null, "GameApiResearch", "alpha", "a1", null, null);
        Assert.Equal(ArtifactKind.ApiFinding, sendHome.Kind);
        Assert.Equal(ArtifactKind.NegativeApiFinding, recall.Kind);
        Assert.Contains("void SendHome()", sendHome.Content); // signature comes from metadata, not from the AI

        // "Game update": SendHome removed, Recall added.
        var newSource = string.Join("\n", FakeGame.AssemblyCSharpSource.Split('\n').Select(l => l.Contains("void SendHome(") ? (l.Contains("force") ? "" : "    public void Recall(int delay) { }") : l));
        Assert.NotEqual(FakeGame.AssemblyCSharpSource, newSource);
        FakeGame.Compile("Assembly-CSharp", newSource, FakeGame.BclFiles().Select(f => (Microsoft.CodeAnalysis.MetadataReference)Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(f)),
            Path.Combine(FakeGame.ManagedPath(gameDir), "Assembly-CSharp.dll"));
        await profiles.ReindexAsync(profile);

        var after = await knowledge.ListAsync(project.Id);
        Assert.True(after.Single(a => a.Id == sendHome.Id).Stale);
        Assert.StartsWith("[STALE after game update]", after.Single(a => a.Id == sendHome.Id).Title);
        Assert.True(after.Single(a => a.Id == recall.Id).Stale);
        Assert.False(after.Single(a => a.Id == isHome.Id).Stale); // still true in the new build → re-verified

        var context = await host.Get<IAIContextBuilder>().BuildAsync(new ContextRequest { Project = project, Profile = await profiles.GetAsync(profile.Id) });
        Assert.Contains("[STALE — game updated, re-verify before use]", context);
        Assert.Contains("[VERIFIED local metadata] EntityDrone.IsHome", context);
    }

    [Fact]
    public async Task Restoring_before_an_AI_change_counts_as_user_revert()
    {
        var alpha = new ScriptedProvider("alpha", "a1");
        alpha.Steps["CSharpHarmony"] = async (req, tools, n) =>
        {
            await Call(tools, ToolNames.ReadProjectFile, new { path = "Harmony/Patches.cs" });
            await Call(tools, ToolNames.ProposeFileChanges, new { summary = "fix", files = new[] { new { path = "Harmony/Patches.cs", content = SampleMod.PatchSource } } });
            return Done();
        };
        using var host = new AITestHost(providers: alpha);
        var (project, _) = await host.ImportBrokenAsync();
        await host.Get<IAgentCoordinator>().RunAgentTaskAsync(project, AgentKind.CSharpHarmony, "Fix", null, null);
        var tracker = host.Get<IModelPerformanceTracker>();
        var record = (await tracker.GetRecordsAsync(project.Id)).Single(r => r.TaskType == AITaskType.HarmonyImplementation);
        Assert.Equal(TaskOutcome.Success, record.Outcome);
        Assert.True(record.CompilePassed);

        var history = host.Get<ProjectService>().History;
        var before = (await history.ListAsync(project)).Single(r => r.Id == record.BeforeRevisionId);
        await history.RestoreAsync(project, before);

        var reverted = (await tracker.GetRecordsAsync(project.Id)).Single(r => r.Id == record.Id);
        Assert.True(reverted.UserReverted);
        var stats = (await tracker.GetStatsAsync()).Single(s => s.TaskType == AITaskType.HarmonyImplementation);
        Assert.Equal(0, stats.Successes);
        Assert.Equal(1, stats.Reverted);
    }

    [Fact]
    public async Task Handoff_contains_project_state_for_the_next_model()
    {
        var alpha = new ScriptedProvider("alpha", "a1");
        using var host = new AITestHost(providers: alpha);
        var (project, profile) = await host.ImportBrokenAsync();
        var knowledge = host.Get<ProjectKnowledgeService>();
        await knowledge.RecordApiClaimAsync(project, profile, "EntityDrone", "GetHomePosition", null, "CSharpHarmony", "alpha", "a1", null, null);
        var pk = await knowledge.Repository.GetProjectAsync(project.Id);
        pk.Objective = "Repair drone home behaviour";
        await knowledge.Repository.SaveProjectAsync(pk);

        var handoff = await host.Get<IAIContextBuilder>().BuildHandoffAsync(project, profile, null, "Fix Patches.cs");

        Assert.Contains("OBJECTIVE:\n  Repair drone home behaviour", handoff);
        Assert.Contains("EntityDrone.GetHomePosition does NOT exist", handoff);
        Assert.Contains("NEXT RECOMMENDED ACTION:\n  Fix Patches.cs", handoff);
        Assert.Contains("V 2.1 (b7)", handoff);
    }
}
