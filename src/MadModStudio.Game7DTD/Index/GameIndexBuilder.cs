using System.Diagnostics;
using System.Globalization;
using System.Xml;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.ModAnalysis.Assemblies;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MadModStudio.Game7DTD.Index;

public sealed record IndexProgress(string Message, int Current, int Total);

public sealed class IndexBuildResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IndexSummary? Summary { get; init; }
    public TimeSpan Duration { get; init; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Builds the searchable knowledge index for one game profile from the installed files: assembly metadata (read via
/// System.Reflection.Metadata, never executed), config XML entries and localization keys. The index lives in a
/// per-profile SQLite file in the cache folder, not in the application database.
/// </summary>
public sealed class GameIndexBuilder
{
    private static readonly string[] FrameworkPrefixes = { "System", "mscorlib", "netstandard", "Mono.", "Microsoft.", "Newtonsoft." };
    private readonly AssemblyInspector _inspector;
    private readonly ILogger<GameIndexBuilder> _log;

    public GameIndexBuilder(AssemblyInspector inspector, ILogger<GameIndexBuilder>? log = null)
    {
        _inspector = inspector;
        _log = log ?? NullLogger<GameIndexBuilder>.Instance;
    }

    /// <summary>Assemblies whose private members are indexed too (Harmony patches frequently target private methods).</summary>
    public static bool IsPrimaryGameAssembly(string fileName) =>
        fileName.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase);

    public static bool IsFrameworkAssembly(string fileName) =>
        FrameworkPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public Task<IndexBuildResult> BuildAsync(GameProfile profile, string indexPath, IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Build(profile, indexPath, progress, ct), ct);

    private IndexBuildResult Build(GameProfile profile, string indexPath, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new IndexBuildResult();
        if (profile.ManagedPath is null || !Directory.Exists(profile.ManagedPath))
            return new IndexBuildResult { Error = $"Managed assembly folder not found: {profile.ManagedPath}" };

        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        var temp = indexPath + ".building";
        if (File.Exists(temp)) File.Delete(temp);

        try
        {
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
            {
                c.Open();
                Exec(c, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF;");
                Exec(c, GameIndexSchema.Create);
                using (var tx = c.BeginTransaction())
                {
                    IndexAssemblies(c, tx, profile, progress, result, ct);
                    if (profile.ConfigPath != null && Directory.Exists(profile.ConfigPath))
                        IndexXml(c, tx, profile.ConfigPath, progress, result, ct);
                    else
                        result.Warnings.Add("Config folder not available; XML was not indexed.");
                    using var meta = c.CreateCommand();
                    meta.Transaction = tx;
                    meta.CommandText = "INSERT INTO meta(key, value) VALUES ('schema', $s), ('built_utc', $b), ('profile', $p), ('game_version', $v)";
                    meta.Parameters.AddWithValue("$s", GameIndexSchema.Version.ToString());
                    meta.Parameters.AddWithValue("$b", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    meta.Parameters.AddWithValue("$p", profile.Id.ToString());
                    meta.Parameters.AddWithValue("$v", (object?)profile.GameVersion ?? DBNull.Value);
                    meta.ExecuteNonQuery();
                    tx.Commit();
                }
                progress?.Report(new IndexProgress("Building search indexes...", 0, 0));
                Exec(c, GameIndexSchema.Indexes);
            }
            SqliteConnection.ClearAllPools();
            File.Move(temp, indexPath, overwrite: true);
            var summary = new SqliteGameKnowledgeIndex(indexPath).GetSummary();
            _log.LogInformation("Indexed game profile {Profile}: {Types} types, {Members} members, {Xml} XML files in {Elapsed}",
                profile.Name, summary.Types, summary.Members, summary.XmlFiles, sw.Elapsed);
            return new IndexBuildResult { Success = true, Summary = summary, Duration = sw.Elapsed }.WithWarnings(result.Warnings);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            _log.LogError(ex, "Indexing failed for {Profile}", profile.Name);
            return new IndexBuildResult { Error = $"Indexing failed: {ex.Message}", Duration = sw.Elapsed };
        }
    }

    private void IndexAssemblies(SqliteConnection c, SqliteTransaction tx, GameProfile profile, IProgress<IndexProgress>? progress, IndexBuildResult result, CancellationToken ct)
    {
        var files = Directory.GetFiles(profile.ManagedPath!, "*.dll")
            .Where(f => !IsFrameworkAssembly(Path.GetFileName(f)))
            .ToList();
        if (profile.HarmonyAssemblyPath != null && File.Exists(profile.HarmonyAssemblyPath)) files.Add(profile.HarmonyAssemblyPath);

        using var insAsm = c.CreateCommand();
        insAsm.Transaction = tx;
        insAsm.CommandText = "INSERT INTO assemblies(name, version, path, size, mtime, include_nonpublic) VALUES ($n,$v,$p,$s,$m,$np); SELECT last_insert_rowid();";
        using var insType = c.CreateCommand();
        insType.Transaction = tx;
        insType.CommandText = "INSERT INTO types(assembly_id, namespace, name, full_name, kind, base_type, is_public, interfaces, attributes) VALUES ($a,$ns,$n,$f,$k,$b,$p,$i,$at); SELECT last_insert_rowid();";
        using var insMember = c.CreateCommand();
        insMember.Transaction = tx;
        insMember.CommandText = "INSERT INTO members(type_id, kind, name, signature, return_type, is_static, is_public, param_count) VALUES ($t,$k,$n,$s,$r,$st,$p,$pc)";
        foreach (var name in new[] { "$a", "$ns", "$n", "$f", "$k", "$b", "$p", "$i", "$at" }) insType.Parameters.Add(new SqliteParameter(name, null));
        foreach (var name in new[] { "$t", "$k", "$n", "$s", "$r", "$st", "$p", "$pc" }) insMember.Parameters.Add(new SqliteParameter(name, null));

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var fileName = Path.GetFileName(file);
            progress?.Report(new IndexProgress($"Reading metadata: {fileName}", i + 1, files.Count));
            var primary = IsPrimaryGameAssembly(fileName);
            var report = _inspector.Inspect(file, new AssemblyInspectionOptions
            {
                IncludeNonPublic = primary,
                IncludeMembers = true,
                ComputeHash = false,
                CollectExternalReferences = false,
            });
            if (!report.Success)
            {
                if (report.ReadError != null && !report.ReadError.StartsWith("Not a managed"))
                    result.Warnings.Add($"{fileName}: {report.ReadError}");
                continue;
            }
            var fi = new FileInfo(file);
            insAsm.Parameters.Clear();
            insAsm.Parameters.AddWithValue("$n", report.Name);
            insAsm.Parameters.AddWithValue("$v", report.Version);
            insAsm.Parameters.AddWithValue("$p", file);
            insAsm.Parameters.AddWithValue("$s", fi.Length);
            insAsm.Parameters.AddWithValue("$m", fi.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture));
            insAsm.Parameters.AddWithValue("$np", primary ? 1 : 0);
            var asmId = Convert.ToInt64(insAsm.ExecuteScalar());

            foreach (var t in report.Types)
            {
                insType.Parameters["$a"].Value = asmId;
                insType.Parameters["$ns"].Value = t.Namespace;
                insType.Parameters["$n"].Value = t.Name;
                insType.Parameters["$f"].Value = t.FullName;
                insType.Parameters["$k"].Value = t.Kind;
                insType.Parameters["$b"].Value = (object?)t.BaseType ?? DBNull.Value;
                insType.Parameters["$p"].Value = t.IsPublic ? 1 : 0;
                insType.Parameters["$i"].Value = string.Join(";", t.Interfaces);
                insType.Parameters["$at"].Value = string.Join(";", t.Attributes);
                var typeId = Convert.ToInt64(insType.ExecuteScalar());
                foreach (var m in t.Members)
                {
                    insMember.Parameters["$t"].Value = typeId;
                    insMember.Parameters["$k"].Value = m.Kind;
                    insMember.Parameters["$n"].Value = m.Name;
                    insMember.Parameters["$s"].Value = m.Signature;
                    insMember.Parameters["$r"].Value = (object?)m.ReturnType ?? DBNull.Value;
                    insMember.Parameters["$st"].Value = m.IsStatic ? 1 : 0;
                    insMember.Parameters["$p"].Value = m.IsPublic ? 1 : 0;
                    insMember.Parameters["$pc"].Value = m.ParameterCount;
                    insMember.ExecuteNonQuery();
                }
            }
        }
    }

    private static void IndexXml(SqliteConnection c, SqliteTransaction tx, string configPath, IProgress<IndexProgress>? progress, IndexBuildResult result, CancellationToken ct)
    {
        using var insFile = c.CreateCommand();
        insFile.Transaction = tx;
        insFile.CommandText = "INSERT INTO xml_files(rel_path, full_path, root, size, entry_count, error) VALUES ($r,$f,$root,$s,$e,$err); SELECT last_insert_rowid();";
        using var insEntry = c.CreateCommand();
        insEntry.Transaction = tx;
        insEntry.CommandText = "INSERT INTO xml_entries(file_id, element, name, path, line) VALUES ($f,$e,$n,$p,$l)";
        foreach (var n in new[] { "$f", "$e", "$n", "$p", "$l" }) insEntry.Parameters.Add(new SqliteParameter(n, null));

        var xmlFiles = Directory.GetFiles(configPath, "*.xml", SearchOption.AllDirectories);
        for (var i = 0; i < xmlFiles.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = xmlFiles[i];
            var rel = PathSafety.GetRelative(configPath, file);
            progress?.Report(new IndexProgress($"Indexing XML: {rel}", i + 1, xmlFiles.Length));
            var entries = new List<(string Element, string? Name, string Path, int Line)>();
            string? root = null; string? error = null;
            try
            {
                using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, XmlResolver = null });
                var stack = new List<string>();
                var info = (IXmlLineInfo)reader;
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        var depth = reader.Depth;
                        while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
                        var elName = reader.LocalName;
                        root ??= elName;
                        var nameAttr = reader.GetAttribute("name") ?? reader.GetAttribute("id");
                        var segment = nameAttr is null ? elName : $"{elName}[@{(reader.GetAttribute("name") != null ? "name" : "id")}='{nameAttr}']";
                        stack.Add(segment);
                        if (nameAttr != null && depth <= 3)
                            entries.Add((elName, nameAttr, "/" + string.Join("/", stack), info.LineNumber));
                        if (reader.IsEmptyElement) stack.RemoveAt(stack.Count - 1);
                    }
                }
            }
            catch (XmlException ex)
            {
                error = ex.Message;
                result.Warnings.Add($"Game XML {rel} could not be fully parsed: {ex.Message}");
            }
            insFile.Parameters.Clear();
            insFile.Parameters.AddWithValue("$r", rel);
            insFile.Parameters.AddWithValue("$f", file);
            insFile.Parameters.AddWithValue("$root", (object?)root ?? DBNull.Value);
            insFile.Parameters.AddWithValue("$s", new FileInfo(file).Length);
            insFile.Parameters.AddWithValue("$e", entries.Count);
            insFile.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
            var fileId = Convert.ToInt64(insFile.ExecuteScalar());
            foreach (var e in entries)
            {
                insEntry.Parameters["$f"].Value = fileId;
                insEntry.Parameters["$e"].Value = e.Element;
                insEntry.Parameters["$n"].Value = (object?)e.Name ?? DBNull.Value;
                insEntry.Parameters["$p"].Value = e.Path;
                insEntry.Parameters["$l"].Value = e.Line;
                insEntry.ExecuteNonQuery();
            }
        }

        using var insLoc = c.CreateCommand();
        insLoc.Transaction = tx;
        insLoc.CommandText = "INSERT INTO localization(key, file, english) VALUES ($k,$f,$e)";
        foreach (var n in new[] { "$k", "$f", "$e" }) insLoc.Parameters.Add(new SqliteParameter(n, null));
        foreach (var loc in Directory.GetFiles(configPath, "Localization*.txt", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new IndexProgress($"Indexing localization: {Path.GetFileName(loc)}", 0, 0));
            try
            {
                foreach (var row in LocalizationFile.ReadRows(loc))
                {
                    insLoc.Parameters["$k"].Value = row.Key;
                    insLoc.Parameters["$f"].Value = PathSafety.GetRelative(configPath, loc);
                    insLoc.Parameters["$e"].Value = (object?)row.English ?? DBNull.Value;
                    insLoc.ExecuteNonQuery();
                }
            }
            catch (IOException ex) { result.Warnings.Add($"Localization {loc}: {ex.Message}"); }
        }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryDelete(string path)
    {
        try { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}

internal static class IndexBuildResultExtensions
{
    public static IndexBuildResult WithWarnings(this IndexBuildResult r, IEnumerable<string> warnings)
    {
        r.Warnings.AddRange(warnings);
        return r;
    }
}
