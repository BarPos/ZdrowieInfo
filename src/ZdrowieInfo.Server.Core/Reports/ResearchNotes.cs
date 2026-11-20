using System.Text.RegularExpressions;

namespace ZdrowieInfo.Server.Reports;

public enum ResearchBlockKind { Heading, Paragraph, Points }

/// <summary>Punkt notatek: „– Poprawki: 1.30.7…” → etykieta „Poprawki”, tekst „1.30.7…”.</summary>
public sealed record ResearchPoint(string? Label, string Text);

public sealed record ResearchBlock(ResearchBlockKind Kind, string Text, IReadOnlyList<ResearchPoint> Points);

/// <summary>Fragment tekstu: zwykły tekst albo adres (link na stronie, \url w PDF – łamie się na marginesie).</summary>
public sealed record TextSegment(string Text, bool IsUrl);

/// <summary>
/// Notatki ze sprawdzania podatności (zwykły tekst modelu) jako bloki do złożenia jak „Ustalenia”: wiersz przed punktami
/// to nagłówek podatności, wiersze „– …” to punkty (z pogrubioną etykietą przed dwukropkiem), reszta – akapity.
/// </summary>
public static partial class ResearchNotes
{
    /// <summary>Najdłuższa etykieta punktu („Komunikat producenta”) – dłuższy tekst przed dwukropkiem to zdanie, nie etykieta.</summary>
    private const int MaxLabel = 40;

    [GeneratedRegex(@"^\s*(?:[–—•*·]|-(?=\s))\s*")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"https?://[^\s<>""]+")]
    private static partial Regex Url();

    public static IReadOnlyList<ResearchBlock> Parse(string? notes)
    {
        var lines = (notes ?? "").Replace("\r\n", "\n").Split('\n')
            .Select(l => Clean(l)).Where(l => l.Length > 0).ToList();
        var blocks = new List<ResearchBlock>();
        List<ResearchPoint>? points = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (Bullet().IsMatch(line))
            {
                if (points is null) blocks.Add(new ResearchBlock(ResearchBlockKind.Points, "", points = []));
                points.Add(Point(Bullet().Replace(line, "", 1)));
                continue;
            }
            points = null;
            var beforePoints = i + 1 < lines.Count && Bullet().IsMatch(lines[i + 1]);
            blocks.Add(new ResearchBlock(beforePoints ? ResearchBlockKind.Heading : ResearchBlockKind.Paragraph, line, []));
        }
        return blocks;
    }

    /// <summary>Tekst z adresami wydzielonymi jako osobne fragmenty (bez kropki czy nawiasu zamykającego zdanie).</summary>
    public static IReadOnlyList<TextSegment> Segments(string? text)
    {
        var segments = new List<TextSegment>();
        if (string.IsNullOrEmpty(text)) return segments;
        var position = 0;
        foreach (Match match in Url().Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '!', '?', '”', '’', '\'');
            if (match.Index > position) segments.Add(new TextSegment(text[position..match.Index], false));
            segments.Add(new TextSegment(url, true));
            position = match.Index + url.Length;
        }
        if (position < text.Length) segments.Add(new TextSegment(text[position..], false));
        return segments;
    }

    /// <summary>Bez znaczników Markdown, których model mógł użyć mimo prośby (**, #).</summary>
    private static string Clean(string line)
    {
        var text = line.Trim().Replace("**", "");
        return text.StartsWith('#') ? text.TrimStart('#').Trim() : text;
    }

    private static ResearchPoint Point(string text)
    {
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        return colon is > 1 and <= MaxLabel && !text[..colon].Contains("http", StringComparison.Ordinal)
            ? new ResearchPoint(text[..colon].Trim(), text[(colon + 2)..].Trim())
            : new ResearchPoint(null, text.Trim());
    }
}
