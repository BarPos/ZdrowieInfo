using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace ZdrowieInfo.Server.Sources.Extraction;

/// <summary>
/// Tekst z HTML z zachowaniem podziału na bloki (akapity, wiersze listy, nagłówki) i adresami linków.
/// Białe znaki normalizowane, żeby ta sama treść zawsze dawała ten sam tekst (i hash).
/// </summary>
public static partial class HtmlText
{
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "main", "aside", "nav", "li", "ul", "ol", "dl", "dt", "dd",
        "h1", "h2", "h3", "h4", "h5", "h6", "table", "thead", "tbody", "tr", "blockquote", "pre", "figure", "figcaption", "form",
    };

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "iframe", "button", "select", "option", "input", "textarea", "picture", "img",
    };

    /// <param name="includeLinkUrls">Dopisuje adres linku: „tekst (https://…)”. Dzięki temu podmiana pliku pod tą samą nazwą też jest zmianą.</param>
    public static string FromElements(IEnumerable<IElement> elements, bool includeLinkUrls)
    {
        var sb = new StringBuilder();
        foreach (var element in elements)
        {
            Walk(element, sb, includeLinkUrls);
            sb.Append('\n');
        }
        return Normalize(sb.ToString());
    }

    public static string Normalize(string text)
    {
        var lines = text.Replace(' ', ' ').Replace("​", "").Replace("­", "").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var collapsed = Whitespace().Replace(line, " ").Trim();
            if (collapsed.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(collapsed);
        }
        return sb.ToString();
    }

    /// <summary>Tekst jednego elementu w jednej linii (tytuły, daty).</summary>
    public static string Inline(IElement? element) =>
        element is null ? "" : Whitespace().Replace(element.TextContent.Replace(' ', ' '), " ").Trim();

    private static void Walk(INode node, StringBuilder sb, bool includeLinkUrls)
    {
        switch (node)
        {
            case IText text:
                sb.Append(text.Data);
                return;
            case IElement element:
                if (Skipped.Contains(element.LocalName)) return;
                if (element.LocalName == "br") { sb.Append('\n'); return; }

                var block = Blocks.Contains(element.LocalName);
                if (block) sb.Append('\n');
                foreach (var child in element.ChildNodes) Walk(child, sb, includeLinkUrls);

                if (includeLinkUrls && element is IHtmlAnchorElement anchor)
                {
                    var href = anchor.GetAttribute("href");
                    if (!string.IsNullOrWhiteSpace(href) && !href.StartsWith('#') && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                        sb.Append(" (").Append(UrlNormalizer.Resolve(anchor.BaseUri, href) ?? href).Append(')');
                }
                if (element.LocalName is "td" or "th") sb.Append(' ');
                if (block) sb.Append('\n');
                return;
        }
    }

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex Whitespace();
}
