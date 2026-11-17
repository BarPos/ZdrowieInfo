using System.Text.RegularExpressions;

namespace ZdrowieInfo.Server.Sources.Fetching;

/// <summary>
/// robots.txt wg RFC 9309: grupa dla naszego produktu („zdrowieinfo”), a gdy jej brak – grupa „*”.
/// Wygrywa najdłuższa pasująca reguła; przy remisie Allow.
/// </summary>
public sealed class RobotsTxt
{
    public const string ProductToken = "zdrowieinfo";

    private readonly List<(bool Allow, string Pattern, Regex Regex)> _rules;

    private RobotsTxt(List<(bool, string, Regex)> rules) => _rules = rules;

    public static readonly RobotsTxt AllowAll = new([]);

    public static RobotsTxt Parse(string content)
    {
        var groups = new List<(List<string> Agents, List<(bool, string)> Rules)>();
        List<string>? agents = null;
        List<(bool, string)>? rules = null;
        var lastWasAgent = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (key == "user-agent")
            {
                if (!lastWasAgent)
                {
                    agents = [];
                    rules = [];
                    groups.Add((agents, rules));
                }
                agents!.Add(value.ToLowerInvariant());
                lastWasAgent = true;
            }
            else if (key is "allow" or "disallow")
            {
                lastWasAgent = false;
                if (rules is null || value.Length == 0) continue; // pusty Disallow = brak reguły
                rules.Add((key == "allow", value));
            }
            else
            {
                lastWasAgent = false;
            }
        }

        var mine = groups.Where(g => g.Agents.Contains(ProductToken)).ToList();
        var chosen = mine.Count > 0 ? mine : groups.Where(g => g.Agents.Contains("*")).ToList();
        return new RobotsTxt(chosen.SelectMany(g => g.Rules).Select(r => (r.Item1, r.Item2, ToRegex(r.Item2))).ToList());
    }

    public bool IsAllowed(string pathAndQuery)
    {
        if (pathAndQuery == "/robots.txt") return true;
        var best = _rules.Where(r => r.Regex.IsMatch(pathAndQuery))
            .OrderByDescending(r => r.Pattern.Length)
            .ThenByDescending(r => r.Allow)
            .FirstOrDefault();
        return best.Pattern is null || best.Allow;
    }

    private static Regex ToRegex(string pattern)
    {
        var anchored = pattern.EndsWith('$');
        var body = Regex.Escape(anchored ? pattern[..^1] : pattern).Replace(@"\*", ".*");
        return new Regex("^" + body + (anchored ? "$" : ""), RegexOptions.CultureInvariant);
    }
}
