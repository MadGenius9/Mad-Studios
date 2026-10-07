using System.Collections.Concurrent;
using System.Text.Json;
using MadModStudio.AI;
using MadModStudio.AI.Agents;
using MadModStudio.AI.Models;
using MadModStudio.Core;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Persistence;
using MadModStudio.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MadModStudio.Game7DTD.Tests;

public delegate Task<AIRunResult> ScriptStep(AIRunRequest request, IAIToolExecutor tools, int callNumber);

/// <summary>
/// A provider without network access that drives the real Mad Mod Studio tools exactly like a model would. Behaviour is
/// scripted per agent (request.Tag) so multi-agent workflows can be tested deterministically.
/// </summary>
public sealed class ScriptedProvider : IAIProvider
{
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private int _active;

    public ScriptedProvider(string id, params string[] models)
    {
        Id = id;
        Models = models;
    }

    public string Id { get; }
    public string DisplayName => "Scripted " + Id;
    public string Destination => "localhost (test)";
    public bool IsConfigured { get; set; } = true;
    public ProviderConnectionStatus Status { get; private set; } = ProviderConnectionStatus.NotConfigured;
    public string[] Models { get; }
    public Dictionary<string, ScriptStep> Steps { get; } = new();
    public ConcurrentBag<AIRunRequest> Requests { get; } = new();
    public int MaxConcurrent { get; private set; }

    public Task<ProviderConnectionStatus> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult(Status = new ProviderConnectionStatus(ConnectionState.Connected, "test", DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProviderModel>>(Models.Select(m => new ProviderModel(Id, m, m, 200_000)).ToList());

    public async Task<AIRunResult> RunAsync(AIRunRequest request, IAIToolExecutor tools, IProgress<AIEvent>? progress = null, CancellationToken ct = default)
    {
        Requests.Add(request);
        var now = Interlocked.Increment(ref _active);
        lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
        try
        {
            var key = request.Tag ?? "";
            var n = _calls.AddOrUpdate(key, 1, (_, v) => v + 1);
            if (!Steps.TryGetValue(key, out var step)) return new AIRunResult { Error = $"No script for {key}", ModelUsed = request.Model };
            // Same lifecycle events the real providers emit around each model call.
            progress?.Report(AIEvent.Now(AIEventKind.RequestStarted, $"Waiting for {request.Model} (turn 1)"));
            var r = await step(request, tools, n);
            progress?.Report(AIEvent.Now(AIEventKind.ResponseReceived, $"{request.Model} replied ({r.Usage.InputTokens:N0} input / {r.Usage.OutputTokens:N0} output tokens)"));
            return r;
        }
        finally { Interlocked.Decrement(ref _active); }
    }

    public static Task<AIToolResult> Call(IAIToolExecutor tools, string name, object args) =>
        tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), CancellationToken.None);

    public static AIRunResult Done(string text = "done", long tokens = 1000) =>
        new() { Success = true, FinalText = text, Usage = new AIUsage { InputTokens = tokens, OutputTokens = tokens / 10 } };
}

/// <summary>Wired services with AI enabled, using only scripted providers.</summary>
public sealed class AITestHost : IDisposable
{
    public AITestHost(IChangeApprovalService? approval = null, Action<AIPolicy>? policy = null, params ScriptedProvider[] providers)
    {
        Root = FakeGame.TempDir("aihome");
        Paths = new AppPaths(Root);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPersistence(Paths);
        services.AddGame7DTD();
        services.AddAI(Paths);
        services.RemoveAll<IAIProvider>();
        foreach (var p in providers) services.AddSingleton<IAIProvider>(p);
        services.AddSingleton<IAIConsentService>(new PreApprovedConsent());
        services.AddSingleton<IChangeApprovalService>(approval ?? new FixedApproval(true));
        Services = services.BuildServiceProvider();
        var pol = Services.GetRequiredService<AIPolicy>();
        pol.ControlLevel = MadModStudio.Core.Knowledge.AgentControlLevel.Automatic;
        pol.AskBeforeEachRun = false;
        policy?.Invoke(pol);
        Services.GetRequiredService<IModelCatalog>().RefreshAsync().GetAwaiter().GetResult();
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public ServiceProvider Services { get; }
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<(ModProject Project, GameProfile Profile)> ImportBrokenAsync(string? patchSource = null)
    {
        var profiles = Get<GameProfileService>();
        var profile = (await profiles.CreateProfileAsync(FakeGame.Shared)).Profile!;
        await profiles.ReindexAsync(profile);
        var broken = patchSource ?? SampleMod.PatchSource.Replace("__instance.IsHome", "__instance.IsAtHomeBase");
        var zip = SampleMod.WriteZip(FakeGame.TempDir("inbox"), patchSource: broken);
        var project = (await Get<ProjectService>().ImportAsync(zip, profile.Id)).Single().Project;
        return (project, profile);
    }

    public void Dispose()
    {
        Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
