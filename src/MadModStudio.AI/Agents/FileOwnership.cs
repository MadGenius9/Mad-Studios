using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MadModStudio.AI.Agents;

/// <summary>
/// File-level write ownership: any number of agents may read a file, but only one may write it at a time. Locks are
/// acquired in a fixed order to avoid deadlocks.
/// </summary>
public sealed class FileOwnershipManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(Guid projectId, string path) => projectId.ToString("N") + "|" + path.Replace('\\', '/');

    public async Task<IAsyncDisposable> AcquireWriteAsync(Guid projectId, IEnumerable<string> paths, string owner, CancellationToken ct = default)
    {
        var keys = paths.Select(p => Key(projectId, p)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var acquired = new List<string>();
        try
        {
            foreach (var k in keys)
            {
                await _locks.GetOrAdd(k, _ => new SemaphoreSlim(1, 1)).WaitAsync(ct).ConfigureAwait(false);
                acquired.Add(k);
                _owners[k] = owner;
            }
        }
        catch
        {
            Release(acquired);
            throw;
        }
        return new Releaser(() => Release(acquired));
    }

    private void Release(List<string> keys)
    {
        foreach (var k in keys)
        {
            _owners.TryRemove(k, out _);
            if (_locks.TryGetValue(k, out var s)) s.Release();
        }
    }

    /// <summary>Current writers for a project (path → owner), for the Agent Board.</summary>
    public IReadOnlyDictionary<string, string> Owners(Guid projectId)
    {
        var prefix = projectId.ToString("N") + "|";
        return _owners.Where(kv => kv.Key.StartsWith(prefix)).ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value);
    }

    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public static string? HashFile(string path) => File.Exists(path) ? Hash(File.ReadAllText(path)) : null;

    private sealed class Releaser : IAsyncDisposable
    {
        private Action? _release;
        public Releaser(Action release) => _release = release;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
