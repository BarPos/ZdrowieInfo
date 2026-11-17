using System.Globalization;
using System.Text.RegularExpressions;
using ZdrowieInfo.Server.Common;

namespace ZdrowieInfo.Server.Sources.Extraction;

/// <summary>Daty z polskich stron: 16.11.2026, 03-11-2026, 2026-11-17, ISO z czasem, „16 listopada 2026 r.”.</summary>
public static partial class PolishDate
{
    private static readonly string[] Months =
        ["stycznia", "lutego", "marca", "kwietnia", "maja", "czerwca", "lipca", "sierpnia", "września", "października", "listopada", "grudnia"];

    public static DateOnly? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        if (text.Length > 10 && text[4] == '-' && text.Contains('T') &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso))
            return DateOnly.FromDateTime(WarsawClock.ToLocal(iso));

        var numeric = Numeric().Match(text);
        if (numeric.Success)
        {
            var g = numeric.Groups;
            int day, month, year;
            if (g["y1"].Success) { year = int.Parse(g["y1"].Value); month = int.Parse(g["m1"].Value); day = int.Parse(g["d1"].Value); }
            else { day = int.Parse(g["d2"].Value); month = int.Parse(g["m2"].Value); year = int.Parse(g["y2"].Value); }
            return Valid(year, month, day);
        }

        var words = Words().Match(text.ToLowerInvariant());
        if (words.Success)
        {
            var month = Array.IndexOf(Months, words.Groups["m"].Value) + 1;
            if (month > 0) return Valid(int.Parse(words.Groups["y"].Value), month, int.Parse(words.Groups["d"].Value));
        }
        return null;
    }

    private static DateOnly? Valid(int y, int m, int d) =>
        y is >= 1990 and <= 2100 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) ? new DateOnly(y, m, d) : null;

    [GeneratedRegex(@"(?<y1>\d{4})-(?<m1>\d{1,2})-(?<d1>\d{1,2})|(?<d2>\d{1,2})[.\-/](?<m2>\d{1,2})[.\-/](?<y2>\d{4})")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"(?<d>\d{1,2})\s+(?<m>[a-ząćęłńóśźż]+)\s+(?<y>\d{4})")]
    private static partial Regex Words();
}
