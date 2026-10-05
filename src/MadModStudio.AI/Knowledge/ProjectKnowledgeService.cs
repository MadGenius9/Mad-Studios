using System.Text.Json;
using System.Text.RegularExpressions;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.Knowledge;
using MadModStudio.Core.Models;

namespace MadModStudio.AI.Knowledge;

public sealed record ApiFindingData(string TypeName, string? MemberName, IReadOnlyList<string> Signatures);

/// <summary>
/// Owns provider-independent project knowledge. API findings are verified against the local game index before they are
/// stored; claims that the index contradicts are stored as negative findings so no agent repeats them.
/// </summary>
public sealed partial class ProjectKnowledgeService : IGameUpdateListener
{
    private readonly IKnowledgeRepository _repo;
    private readonly Func<Guid, IGameKnowledgeIndex?> _indexForProfile;

    public ProjectKnowledgeService(IKnowledgeRepository repo, Func<Guid, IGameKnowledgeIndex?> indexForProfile)
    {
        _repo = repo;
        _indexForProfile = indexForProfile;
    }

    public IKnowledgeRepository Repository => _repo;

    public Task<KnowledgeArtifact> AddAsync(KnowledgeArtifact a, CancellationToken ct = default) => _repo.AddAsync(a, ct);

    public Task<IReadOnlyList<KnowledgeArtifact>> ListAsync(Guid projectId, ArtifactKind? kind = null, int limit = 500, CancellationToken ct = default) =>
        _repo.ListAsync(projectId, kind, limit, ct);

    /// <summary>
    /// Verifies a type/member claim against the installed game's index. Returns the stored artifact (ApiFinding when it
    /// exists, NegativeApiFinding when it does not).
    /// </summary>
    public async Task<KnowledgeArtifact> RecordApiClaimAsync(ModProject project, GameProfile? profile, string typeName, string? memberName, string? note,
        string? agent, string? provider, string? model, string? taskId, string? runId, CancellationToken ct = default)
    {
        var index = profile is null ? null : _indexForProfile(profile.Id);
        var artifact = new KnowledgeArtifact
        {
            ProjectId = project.Id,
            Agent = agent, Provider = provider, Model = model, TaskId = taskId, RunId = runId,
            GameProfileId = profile?.Id,
            GameFingerprint = profile?.AssemblyFingerprint,
        };
        if (index is null)
        {
            artifact.Kind = ArtifactKind.Finding;
            artifact.Title = $"UNVERIFIED claim: {typeName}{(memberName is null ? "" : "." + memberName)}";
            artifact.Content = "No indexed Game Profile — this API claim could not be verified and must not be relied on. " + note;
            return await _repo.AddAsync(artifact, ct).ConfigureAwait(false);
        }
        var type = index.GetType(typeName);
        if (type is null)
        {
            artifact.Kind = ArtifactKind.NegativeApiFinding;
            artifact.Verified = true;
            artifact.Source = "Local assembly metadata";
            artifact.Title = $"Type {typeName} does NOT exist in the current Game Profile";
            artifact.Content = $"Type '{typeName}' is not present in the indexed assemblies of {profile!.Name} ({profile.GameVersion ?? "version unknown"}). Do not use it.";
            artifact.DataJson = JsonSerializer.Serialize(new ApiFindingData(typeName, memberName, Array.Empty<string>()));
            return await _repo.AddAsync(artifact, ct).ConfigureAwait(false);
        }
        if (memberName is null)
        {
            var members = index.GetMembers(type.FullName, limit: 200);
            artifact.Kind = ArtifactKind.ApiFinding;
            artifact.Verified = true;
            artifact.Source = "Local assembly metadata";
            artifact.Title = $"TYPE {type.FullName} : {type.BaseType} [{type.Assembly}]";
            artifact.Content = $"Verified from local assembly metadata ({profile!.GameVersion ?? "version unknown"}).{(note is null ? "" : " Note: " + note)}\n" +
                string.Join("\n", members.Take(60).Select(m => "  " + m.Signature));
            artifact.DataJson = JsonSerializer.Serialize(new ApiFindingData(type.FullName, null, Array.Empty<string>()));
            return await _repo.AddAsync(artifact, ct).ConfigureAwait(false);
        }
        var found = index.FindMemberInHierarchy(type.FullName, memberName);
        if (found.Count == 0)
        {
            artifact.Kind = ArtifactKind.NegativeApiFinding;
            artifact.Verified = true;
            artifact.Source = "Local assembly metadata";
            artifact.Title = $"{type.FullName}.{memberName} does NOT exist in the current Game Profile";
            artifact.Content = $"'{memberName}' is not a member of {type.FullName} or its base types in {profile!.Name} ({profile.GameVersion ?? "version unknown"}). Do not use it.";
            artifact.DataJson = JsonSerializer.Serialize(new ApiFindingData(type.FullName, memberName, Array.Empty<string>()));
            return await _repo.AddAsync(artifact, ct).ConfigureAwait(false);
        }
        artifact.Kind = ArtifactKind.ApiFinding;
        artifact.Verified = true;
        artifact.Source = "Local assembly metadata";
        artifact.Title = $"{found[0].DeclaringType}.{memberName}";
        artifact.Content = $"Verified from local assembly metadata ({profile!.GameVersion ?? "version unknown"}):\n" +
            string.Join("\n", found.Select(m => $"  {m.DeclaringType}: {(m.IsPublic ? "public " : "non-public ")}{m.Signature}")) +
            (note is null ? "" : "\nNote: " + note);
        artifact.DataJson = JsonSerializer.Serialize(new ApiFindingData(found[0].DeclaringType, memberName, found.Select(f => f.Signature).ToList()));
        return await _repo.AddAsync(artifact, ct).ConfigureAwait(false);
    }

    [GeneratedRegex(@"'(?<type>[\w.+`<>]+)' does not contain a definition for '(?<member>\w+)'")]
    private static partial Regex Cs1061();

    [GeneratedRegex(@"The (?:type or namespace )?name '(?<name>\w+)' (?:does not exist|could not be found)")]
    private static partial Regex Cs0103();

    /// <summary>
    /// Records a failed attempt with a cause derived from actual tool output (and verified against the index where the
    /// diagnostic names a missing member), so later agents/models receive "DO NOT REPEAT" guidance.
    /// </summary>
    public async Task<KnowledgeArtifact> RecordFailedAttemptAsync(ModProject project, GameProfile? profile, string attempt,
        IReadOnlyList<ModDiagnostic> diagnostics, string? agent, string? provider, string? model, string? taskId, string? runId, CancellationToken ct = default)
    {
        var index = profile is null ? null : _indexForProfile(profile.Id);
        var causes = new List<string>();
        foreach (var d in diagnostics.Where(d => d.Severity == Severity.Error).Take(10))
        {
            var m = Cs1061().Match(d.Message);
            if (m.Success && index != null)
            {
                var type = m.Groups["type"].Value;
                var member = m.Groups["member"].Value;
                var exists = index.GetType(type) is { } t && index.FindMemberInHierarchy(t.FullName, member).Count > 0;
                causes.Add(exists
                    ? $"{d.Code}: {type}.{member} exists in the game, but the call doesn't match it (check accessibility/arguments)."
                    : $"{d.Code}: {type}.{member} is not present in the current Game Profile. DO NOT use it.");
                if (!exists)
                    await RecordApiClaimAsync(project, profile, type, member, "Derived from compiler error " + d.Code, agent, provider, model, taskId, runId, ct).ConfigureAwait(false);
            }
            else causes.Add($"{d.Code}: {d.Message}{(d.FilePath != null ? $" ({d.FilePath}:{d.Line})" : "")}");
        }
        return await _repo.AddAsync(new KnowledgeArtifact
        {
            ProjectId = project.Id,
            Kind = ArtifactKind.FailedAttempt,
            Title = $"Failed attempt by {agent ?? "agent"} ({model ?? "unknown model"})",
            Content = $"ATTEMPT: {attempt}\nRESULT: {diagnostics.Count(x => x.Severity == Severity.Error)} error(s)\nCAUSE:\n" + string.Join("\n", causes.Select(c => "  - " + c)),
            Agent = agent, Provider = provider, Model = model, TaskId = taskId, RunId = runId,
            GameProfileId = profile?.Id, GameFingerprint = profile?.AssemblyFingerprint,
            Verified = true,
            Source = "Local tool (compiler/validators)",
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Stores a reusable success pattern for the same game profile/fingerprint (cross-project).</summary>
    public Task<KnowledgeArtifact> RecordSuccessPatternAsync(ModProject project, GameProfile? profile, string title, string content, string? agent, string? model, CancellationToken ct = default) =>
        _repo.AddAsync(new KnowledgeArtifact
        {
            ProjectId = null,
            Kind = ArtifactKind.SuccessPattern,
            Title = title,
            Content = $"From project {project.Name} — compiled and validated.\n{content}",
            Agent = agent, Model = model,
            GameProfileId = profile?.Id, GameFingerprint = profile?.AssemblyFingerprint,
            Verified = true,
            Source = "Local tool (compiler/validators)",
        }, ct);

    /// <summary>After a game update: mark old knowledge stale, then automatically re-verify structured API findings.</summary>
    public async Task OnGameAssembliesChangedAsync(Guid gameProfileId, string? previousFingerprint, string newFingerprint, CancellationToken ct = default)
    {
        if (previousFingerprint is null) return; // first index: nothing to invalidate
        await _repo.MarkStaleAsync(gameProfileId, newFingerprint, ct).ConfigureAwait(false);
        var index = _indexForProfile(gameProfileId);
        if (index is null) return;
        // Re-verification only touches findings that carry structured type/member data.
        foreach (var projectId in await ProjectsWithFindings(gameProfileId, ct).ConfigureAwait(false))
        {
            foreach (var a in (await _repo.ListAsync(projectId, null, 2000, ct).ConfigureAwait(false))
                     .Where(a => a.Stale && a.Kind is ArtifactKind.ApiFinding or ArtifactKind.NegativeApiFinding && a.DataJson != null))
            {
                var data = JsonSerializer.Deserialize<ApiFindingData>(a.DataJson!);
                if (data is null) continue;
                var type = index.GetType(data.TypeName);
                var exists = type != null && (data.MemberName is null || index.FindMemberInHierarchy(type.FullName, data.MemberName).Count > 0);
                var stillTrue = a.Kind == ArtifactKind.ApiFinding ? exists : !exists;
                a.Stale = !stillTrue;
                a.GameFingerprint = stillTrue ? newFingerprint : a.GameFingerprint;
                if (!stillTrue) a.Title = "[STALE after game update] " + a.Title;
                await _repo.UpdateAsync(a, ct).ConfigureAwait(false);
            }
        }
    }

    private Task<IReadOnlyList<Guid>> ProjectsWithFindings(Guid gameProfileId, CancellationToken ct) => _repo.ListProjectIdsAsync(gameProfileId, ct);
}
