using System.Text;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace ZdrowieInfo.Server.Sources.Checking;

/// <summary>Zmienione fragmenty strony: linie „+ ” (dodane) i „- ” (usunięte) z jedną linią kontekstu.</summary>
public static class PageDiff
{
    public static string Build(string oldText, string newText, int context = 1)
    {
        var lines = InlineDiffBuilder.Diff(oldText, newText, ignoreWhiteSpace: false).Lines;
        var changed = lines.Select((l, i) => (l, i)).Where(x => x.l.Type is ChangeType.Inserted or ChangeType.Deleted).Select(x => x.i).ToHashSet();
        var sb = new StringBuilder();
        var lastWritten = -2;
        for (var i = 0; i < lines.Count; i++)
        {
            var near = changed.Contains(i) || Enumerable.Range(i - context, 2 * context + 1).Any(changed.Contains);
            if (!near) continue;
            if (lastWritten >= 0 && i > lastWritten + 1) sb.Append("…\n");
            var prefix = lines[i].Type switch
            {
                ChangeType.Inserted => "+ ",
                ChangeType.Deleted => "- ",
                _ => "  ",
            };
            sb.Append(prefix).Append(lines[i].Text).Append('\n');
            lastWritten = i;
        }
        return sb.ToString().TrimEnd('\n');
    }
}
