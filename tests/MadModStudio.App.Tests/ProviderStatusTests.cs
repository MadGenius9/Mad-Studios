using MadModStudio.AI;
using MadModStudio.App.ViewModels;

namespace MadModStudio.App.Tests;

/// <summary>"Never display Connected unless a real connection can be verified."</summary>
public class ProviderStatusTests
{
    private sealed class FakeProvider : IAIProvider
    {
        public string Id => "fake";
        public string DisplayName => "Fake";
        public string Destination => "fake.example";
        public bool IsConfigured { get; set; }
        public ProviderConnectionStatus Status { get; set; } = new(ConnectionState.NotConfigured, "Not configured", null);
        public Task<ProviderConnectionStatus> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(Status);
        public Task<IReadOnlyList<MadModStudio.AI.Models.ProviderModel>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MadModStudio.AI.Models.ProviderModel>>(Array.Empty<MadModStudio.AI.Models.ProviderModel>());
        public Task<AIRunResult> RunAsync(AIRunRequest request, IAIToolExecutor tools, IProgress<AIEvent>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void Status_reads_connected_only_after_a_successful_test()
    {
        var provider = new FakeProvider();
        var item = new ProviderItem { Provider = provider };

        item.Refresh();
        Assert.Equal("NOT CONFIGURED", item.Status);

        provider.IsConfigured = true; // key stored, never tested
        item.Refresh();
        Assert.Equal(ConnectionState.Unverified, item.State);
        Assert.DoesNotContain("CONNECTED", item.Status);

        provider.Status = new ProviderConnectionStatus(ConnectionState.Failed, "401 key rejected", DateTimeOffset.UtcNow);
        item.Refresh();
        Assert.StartsWith("CONNECTION FAILED", item.Status);

        provider.Status = new ProviderConnectionStatus(ConnectionState.Connected, "3 models", DateTimeOffset.UtcNow);
        item.Refresh();
        Assert.StartsWith("CONNECTED", item.Status);
    }
}
