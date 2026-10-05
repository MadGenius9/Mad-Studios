using System.Text;
using MadModStudio.Core.History;
using MadModStudio.Core.Knowledge;
using MadModStudio.Core.Models;

namespace MadModStudio.AI.Knowledge;

[Flags]
public enum ContextSection
{
    None = 0,
    ProjectSummary = 1,
    CurrentTask = 2,
    GameProfile = 4,
    ApiFindings = 8,
    DoNotRepeat = 16,
    LogFindings = 32,
    BuildStatus = 64,
    RecentChanges = 128,
    OtherFindings = 256,
    SuccessPatterns = 512,
    UserInstructions = 1024,
    All = 2047,
}

public sealed class ContextRequest
{
    public required ModProject Project { get; init; }
    public GameProfile? Profile { get; init; }
    public AgentTaskRecord? Task { get; init; }
    public ContextSection Sections { get; init; } = ContextSection.All;
    public string? UserInstructions { get; init; }
    public int MaxChars { get; init; } = 40_000;
}

public interface IAIContextBuilder
{
    Task<string> BuildAsync(ContextRequest request, CancellationToken ct = default);
    Task<string> BuildHandoffAsync(ModProject project, GameProfile? profile, AgentTaskRecord? task, string? nextAction, CancellationToken ct = default);
}

/// <summary>
/// Builds compact, relevant context from Mad Mod Studio's own knowledge. Nothing is bulk-uploaded: file contents are
/// fetched by agents through tools, and only the sections a task needs are included.
/// </summary>
public sealed class AIContextBuilder : IAIContextBuilder
{
    private readonly ProjectKnowledgeService _knowledge;
    private readonly ProjectHistoryService _history;

    public AIContextBuilder(ProjectKnowledgeService knowledge, ProjectHistoryService history)
    {
        _knowledge = knowledge;
        _history = history;
    }

    public async Task<string> BuildAsync(ContextRequest r, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var artifacts = await _knowledge.ListAsync(r.Project.Id, null, 400, ct).ConfigureAwait(false);
        var pk = await _knowledge.Repository.GetProjectAsync(r.Project.Id, ct).ConfigureAwait(false);
        void Section(string title, string body, int max)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            sb.AppendLine($"## {title}");
            sb.AppendLine(body.Length > max ? body[..max] + "\n…(truncated)" : body.TrimEnd());
            sb.AppendLine();
        }
        var s = r.Sections;

        if (s.HasFlag(ContextSection.ProjectSummary))
            Section("PROJECT", $"""
                Name: {r.Project.Name} {r.Project.Version} (mod folder '{r.Project.ModFolderName}', type {r.Project.ModType})
                Objective: {pk.Objective ?? "(not set)"}
                Summary: {pk.Summary ?? "(none)"}
                Server-Side Only: {(r.Project.ServerSideOnly ? "YES — prefer solutions that do not require client installation; never claim certainty" : "no")}
                Current side assessment: {r.Project.SideRequirement}; EAC assessment: {r.Project.EacCompatibility}
                """, 3000);
        if (s.HasFlag(ContextSection.CurrentTask) && r.Task != null)
            Section("CURRENT TASK", $"{r.Task.Title}\n{r.Task.Instructions}", 4000);
        if (s.HasFlag(ContextSection.UserInstructions) && !string.IsNullOrWhiteSpace(r.UserInstructions))
            Section("USER INSTRUCTIONS", r.UserInstructions!, 3000);
        if (s.HasFlag(ContextSection.GameProfile))
            Section("GAME PROFILE", r.Profile is null
                ? "No Game Profile assigned: game APIs and XML cannot be verified. Say so instead of guessing."
                : $"{r.Profile.Name} — version {r.Profile.GameVersion ?? "unknown"} ({r.Profile.GameVersionSource}); index {r.Profile.IndexStatus}; runtime {r.Profile.Compilation.DetectedTargetRuntime}; C# {r.Profile.Compilation.LanguageVersion}; Harmony {(r.Profile.HarmonyAssemblyPath != null ? "available" : "not found")}.", 1500);
        if (s.HasFlag(ContextSection.DoNotRepeat))
        {
            var dont = artifacts.Where(a => a.Kind is ArtifactKind.FailedAttempt or ArtifactKind.NegativeApiFinding).Take(25)
                .Select(a => $"- {a.Title}\n  {a.Content.Replace("\n", "\n  ")}");
            Section("DO NOT REPEAT (verified by local tools)", string.Join("\n", dont), 8000);
        }
        if (s.HasFlag(ContextSection.ApiFindings))
        {
            var api = artifacts.Where(a => a.Kind == ArtifactKind.ApiFinding).Take(30)
                .Select(a => $"- {(a.Stale ? "[STALE — game updated, re-verify before use] " : "[VERIFIED local metadata] ")}{a.Title}\n  {a.Content.Replace("\n", "\n  ")}");
            Section("GAME API FINDINGS", string.Join("\n", api), 10000);
        }
        if (s.HasFlag(ContextSection.BuildStatus))
        {
            var build = artifacts.FirstOrDefault(a => a.Kind == ArtifactKind.BuildResult);
            var validation = artifacts.FirstOrDefault(a => a.Kind == ArtifactKind.ValidationResult);
            Section("LATEST BUILD (actual compiler output)", build?.Content ?? "No build has run yet.", 6000);
            Section("LATEST VALIDATION (actual validator output)", validation?.Content ?? "", 6000);
        }
        if (s.HasFlag(ContextSection.LogFindings))
        {
            var logs = artifacts.Where(a => a.Kind is ArtifactKind.LogFinding or ArtifactKind.Diagnostic).Take(10).Select(a => $"- {a.Title}\n  {a.Content.Replace("\n", "\n  ")}");
            Section("LOG FINDINGS", string.Join("\n", logs), 8000);
        }
        if (s.HasFlag(ContextSection.OtherFindings))
        {
            var other = artifacts.Where(a => a.Kind is ArtifactKind.Finding or ArtifactKind.XmlFinding or ArtifactKind.Evidence or ArtifactKind.Decision or ArtifactKind.Review)
                .Where(a => r.Task is null || a.TaskId != r.Task.Id).Take(20)
                .Select(a => $"- [{a.Kind} by {a.Agent ?? "tool"}{(a.Model != null ? " / " + a.Model : "")}] {a.Title}\n  {a.Content.Replace("\n", "\n  ")}");
            Section("FINDINGS FROM OTHER AGENTS", string.Join("\n", other), 8000);
        }
        if (s.HasFlag(ContextSection.SuccessPatterns) && r.Profile != null)
        {
            var patterns = (await _knowledge.Repository.ListGlobalAsync(r.Profile.Id, ArtifactKind.SuccessPattern, 10, ct).ConfigureAwait(false))
                .Where(p => p.GameFingerprint == r.Profile.AssemblyFingerprint && !p.Stale)
                .Select(p => $"- {p.Title}\n  {p.Content.Replace("\n", "\n  ")}");
            Section("PREVIOUSLY SUCCESSFUL PATTERNS (same game build — still verify APIs)", string.Join("\n", patterns), 4000);
        }
        if (s.HasFlag(ContextSection.RecentChanges))
        {
            var revs = (await _history.ListAsync(r.Project, ct).ConfigureAwait(false)).Take(8)
                .Select(v => $"- {v.TimestampUtc.LocalDateTime:g} {v.Action}{(v.Metadata.TryGetValue("agent", out var ag) ? $" [{ag} / {v.Metadata.GetValueOrDefault("model")}]" : "")}: {string.Join(", ", v.ChangedFiles.Take(6))}{(v.BuildStatus != null ? " → " + v.BuildStatus : "")}");
            Section("RECENT CHANGES", string.Join("\n", revs), 3000);
        }
        var text = sb.ToString();
        return text.Length > r.MaxChars ? text[..r.MaxChars] + "\n…(context truncated)" : text;
    }

    public async Task<string> BuildHandoffAsync(ModProject project, GameProfile? profile, AgentTaskRecord? task, string? nextAction, CancellationToken ct = default)
    {
        var artifacts = await _knowledge.ListAsync(project.Id, null, 400, ct).ConfigureAwait(false);
        var pk = await _knowledge.Repository.GetProjectAsync(project.Id, ct).ConfigureAwait(false);
        string List(IEnumerable<KnowledgeArtifact> items, int n) => string.Join("\n", items.Take(n).Select(a => $"  - {a.Title}")) is { Length: > 0 } t ? t : "  (none)";
        var build = artifacts.FirstOrDefault(a => a.Kind == ArtifactKind.BuildResult);
        var validation = artifacts.FirstOrDefault(a => a.Kind == ArtifactKind.ValidationResult);
        var files = (task?.FilesWritten ?? new()).Concat(task?.FilesRead ?? new()).Distinct().ToList();
        return $"""
            PROJECT:
              {project.Name} {project.Version} ({project.ModFolderName})

            OBJECTIVE:
              {pk.Objective ?? "(not set)"}

            CURRENT TASK:
              {(task is null ? "(none)" : $"{task.Agent}: {task.Title} — state {task.State}")}

            CURRENT FINDINGS:
            {List(artifacts.Where(a => a.Kind is ArtifactKind.Finding or ArtifactKind.LogFinding or ArtifactKind.XmlFinding or ArtifactKind.Decision), 12)}

            GAME PROFILE:
              {(profile is null ? "(none)" : $"{profile.Name} — {profile.GameVersion ?? "version unknown"}")}

            RELEVANT API FINDINGS (verified locally):
            {List(artifacts.Where(a => a.Kind == ArtifactKind.ApiFinding && !a.Stale), 15)}

            DO NOT REPEAT:
            {List(artifacts.Where(a => a.Kind is ArtifactKind.NegativeApiFinding or ArtifactKind.FailedAttempt), 15)}

            FILES INVOLVED:
              {(files.Count == 0 ? "(none recorded)" : string.Join(", ", files))}

            COMPILER STATUS:
              {build?.Title ?? "No build yet"}

            VALIDATION STATUS:
              {validation?.Title ?? "Not validated yet"}

            PREVIOUS ATTEMPTS:
            {List(artifacts.Where(a => a.Kind is ArtifactKind.ProposedChange or ArtifactKind.ImplementationResult or ArtifactKind.FailedAttempt), 10)}

            NEXT RECOMMENDED ACTION:
              {nextAction ?? "Review the latest build/validation results and continue from the findings above."}
            """;
    }
}
