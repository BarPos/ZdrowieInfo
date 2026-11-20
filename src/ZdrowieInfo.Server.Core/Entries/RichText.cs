using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ZdrowieInfo.Server.Sources.Extraction;

namespace ZdrowieInfo.Server.Entries;

/// <summary>
/// Treść wpisu własnego (wklejona z poczty albo strony): HTML przebudowany z listy dozwolonych znaczników – nigdy
/// nie przepisywany „z usunięciem złych węzłów” – oraz tekst dla modelu i sprawdzania cytatów (wiersze tabel jako „a | b | c”).
/// </summary>
public static class RichText
{
    /// <summary>Najdłuższy przyjmowany HTML (po oczyszczeniu w przeglądarce).</summary>
    public const int MaxHtmlChars = 300_000;

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "b", "strong", "i", "em", "u", "s", "sub", "sup", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6",
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "blockquote", "pre", "code", "a", "hr",
    };

    /// <summary>Treść tych znaczników znika razem z nimi (reszta nieznanych znaczników zostawia sam tekst).</summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "math", "iframe", "object", "embed", "head", "title", "meta", "link",
        "button", "select", "option", "textarea", "input", "img", "picture", "video", "audio", "canvas", "form",
    };

    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "li", "ul", "ol", "h1", "h2", "h3", "h4", "h5", "h6", "table", "thead", "tbody", "tfoot", "caption", "blockquote", "pre", "hr",
    };

    private static readonly HtmlParser Parser = new();

    /// <summary>Bezpieczny HTML: tylko znaczniki z listy, z atrybutów tylko href (http, https, mailto) oraz colspan i rowspan.</summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var document = Parser.ParseDocument($"<body>{html}</body>");
        var sb = new StringBuilder();
        foreach (var child in document.Body!.ChildNodes) Write(child, sb);
        return sb.ToString().Trim();
    }

    private static void Write(INode node, StringBuilder sb)
    {
        switch (node)
        {
            case IText text:
                sb.Append(Encode(text.Data));
                return;
            case IElement element:
                var name = element.LocalName.ToLowerInvariant();
                if (Dropped.Contains(name)) return;
                if (!Allowed.Contains(name))
                {
                    // div, span, font… – sam tekst, a bloki jako akapity.
                    var block = name is "div" or "section" or "article" or "center";
                    if (block) sb.Append("<p>");
                    foreach (var child in element.ChildNodes) Write(child, sb);
                    if (block) sb.Append("</p>");
                    return;
                }
                sb.Append('<').Append(name);
                if (name == "a" && SafeHref(element.GetAttribute("href")) is { } href)
                    sb.Append(" href=\"").Append(Encode(href)).Append("\" rel=\"noopener nofollow\" target=\"_blank\"");
                if (name is "td" or "th")
                    foreach (var attribute in new[] { "colspan", "rowspan" })
                        if (int.TryParse(element.GetAttribute(attribute), out var span) && span is > 1 and <= 50)
                            sb.Append(' ').Append(attribute).Append("=\"").Append(span).Append('"');
                if (name is "br" or "hr") { sb.Append('>'); return; }
                sb.Append('>');
                foreach (var child in element.ChildNodes) Write(child, sb);
                sb.Append("</").Append(name).Append('>');
                return;
        }
    }

    /// <summary>Tylko znaki specjalne HTML – polskie litery zostają wprost (bez &amp;#243;).</summary>
    private static string Encode(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string? SafeHref(string? href) =>
        Uri.TryCreate(href?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" ? uri.ToString() : null;

    /// <summary>Tekst z (oczyszczonego) HTML: bloki w osobnych liniach, wiersz tabeli jako „komórka | komórka”, linki z adresem.</summary>
    public static string ToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var document = Parser.ParseDocument($"<body>{html}</body>");
        var sb = new StringBuilder();
        foreach (var child in document.Body!.ChildNodes) Text(child, sb);
        return HtmlText.Normalize(sb.ToString());
    }

    private static void Text(INode node, StringBuilder sb)
    {
        switch (node)
        {
            case IText text:
                sb.Append(text.Data);
                return;
            case IElement element:
                var name = element.LocalName.ToLowerInvariant();
                if (Dropped.Contains(name)) return;
                if (name == "br") { sb.Append('\n'); return; }
                if (name == "tr")
                {
                    var cells = element.Children.Where(c => c.LocalName is "td" or "th").Select(c =>
                    {
                        var cell = new StringBuilder();
                        foreach (var child in c.ChildNodes) Text(child, cell);
                        return HtmlText.Normalize(cell.ToString()).Replace('\n', ' ');
                    });
                    sb.Append('\n').Append(string.Join(" | ", cells)).Append('\n');
                    return;
                }
                var block = Blocks.Contains(name);
                if (block) sb.Append('\n');
                if (name == "li") sb.Append("– ");
                foreach (var child in element.ChildNodes) Text(child, sb);
                if (name == "a" && element.GetAttribute("href") is { Length: > 0 } href && !href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                    && !element.TextContent.Contains(href, StringComparison.Ordinal))
                    sb.Append(" (").Append(href).Append(')');
                if (block) sb.Append('\n');
                return;
        }
    }
}
