using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace MadModStudio.ModAnalysis.Comparison;

public enum DiffRowKind { Unchanged, Removed, Added, Modified, Gap }

/// <summary>One aligned row of a side-by-side diff. Line numbers are 1-based; null where the side has no line.</summary>
public sealed record SideBySideRow(int? OldLine, string OldText, int? NewLine, string NewText, DiffRowKind Kind);

public static class TextDiff
{
    /// <summary>
    /// Aligned side-by-side rows. Unchanged stretches longer than 2×<paramref name="context"/> lines are collapsed into
    /// a single <see cref="DiffRowKind.Gap"/> row ("… N unchanged lines …").
    /// </summary>
    public static IReadOnlyList<SideBySideRow> SideBySide(string oldText, string newText, int context = 3)
    {
        static string Norm(string t) => t.Replace("\r\n", "\n") is var n && n.EndsWith('\n') ? n[..^1] : t.Replace("\r\n", "\n");
        var model = SideBySideDiffBuilder.Diff(Norm(oldText), Norm(newText), ignoreWhiteSpace: false, ignoreCase: false);
        var rows = new List<SideBySideRow>();
        for (var i = 0; i < Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count); i++)
        {
            var o = i < model.OldText.Lines.Count ? model.OldText.Lines[i] : null;
            var n = i < model.NewText.Lines.Count ? model.NewText.Lines[i] : null;
            var oldReal = o is { Type: not ChangeType.Imaginary };
            var newReal = n is { Type: not ChangeType.Imaginary };
            var kind = !oldReal ? DiffRowKind.Added
                : !newReal ? DiffRowKind.Removed
                : o!.Type == ChangeType.Unchanged && n!.Type == ChangeType.Unchanged ? DiffRowKind.Unchanged
                : DiffRowKind.Modified;
            rows.Add(new SideBySideRow(oldReal ? o!.Position : null, oldReal ? o!.Text ?? "" : "", newReal ? n!.Position : null, newReal ? n!.Text ?? "" : "", kind));
        }

        // Collapse long unchanged runs, keeping `context` lines next to each change.
        var keep = new bool[rows.Count];
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Kind != DiffRowKind.Unchanged)
                for (var j = Math.Max(0, i - context); j <= Math.Min(rows.Count - 1, i + context); j++) keep[j] = true;
        var result = new List<SideBySideRow>();
        for (var i = 0; i < rows.Count;)
        {
            if (keep[i]) { result.Add(rows[i]); i++; continue; }
            var start = i;
            while (i < rows.Count && !keep[i]) i++;
            result.Add(new SideBySideRow(null, $"… {i - start} unchanged line(s) …", null, "", DiffRowKind.Gap));
        }
        return result;
    }

    /// <summary>Produces a unified diff (like `git diff`) with the given amount of context.</summary>
    public static (string Diff, int Added, int Removed) Unified(string oldText, string newText, string oldName, string newName, int context = 3)
    {
        // A trailing newline terminates the last line; it is not an extra empty line (matches git's line counting).
        static string Norm(string t) => t.Replace("\r\n", "\n") is var n && n.EndsWith('\n') ? n[..^1] : t.Replace("\r\n", "\n");
        var model = InlineDiffBuilder.Diff(Norm(oldText), Norm(newText), ignoreWhiteSpace: false, ignoreCase: false);
        var lines = model.Lines;
        var added = lines.Count(l => l.Type == ChangeType.Inserted);
        var removed = lines.Count(l => l.Type == ChangeType.Deleted);
        if (added == 0 && removed == 0) return ("", 0, 0);

        var sb = new StringBuilder();
        sb.Append("--- ").AppendLine(oldName);
        sb.Append("+++ ").AppendLine(newName);

        // Track old/new line numbers for each inline line.
        var oldNo = new int[lines.Count];
        var newNo = new int[lines.Count];
        int o = 0, n = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Type != ChangeType.Inserted) o++;
            if (lines[i].Type != ChangeType.Deleted) n++;
            oldNo[i] = o;
            newNo[i] = n;
        }

        var changed = Enumerable.Range(0, lines.Count).Where(i => lines[i].Type is ChangeType.Inserted or ChangeType.Deleted).ToList();
        var idx = 0;
        while (idx < changed.Count)
        {
            var start = Math.Max(0, changed[idx] - context);
            var end = Math.Min(lines.Count - 1, changed[idx] + context);
            while (idx + 1 < changed.Count && changed[idx + 1] - context <= end + 1)
            {
                idx++;
                end = Math.Min(lines.Count - 1, changed[idx] + context);
            }
            idx++;
            var oldStart = lines[start].Type == ChangeType.Inserted ? oldNo[start] + 1 : oldNo[start];
            var newStart = lines[start].Type == ChangeType.Deleted ? newNo[start] + 1 : newNo[start];
            var oldCount = Enumerable.Range(start, end - start + 1).Count(i => lines[i].Type != ChangeType.Inserted);
            var newCount = Enumerable.Range(start, end - start + 1).Count(i => lines[i].Type != ChangeType.Deleted);
            sb.AppendLine($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@");
            for (var i = start; i <= end; i++)
            {
                var prefix = lines[i].Type switch { ChangeType.Inserted => '+', ChangeType.Deleted => '-', _ => ' ' };
                sb.Append(prefix).AppendLine(lines[i].Text);
            }
        }
        return (sb.ToString(), added, removed);
    }
}
