using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using MimeKit;
using MimeKit.Utils;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Sources.Extraction;

namespace ZdrowieInfo.Server.Entries;

/// <summary>Komunikat odczytany z maila – gotowy do zapisu jako wpis własny.</summary>
/// <param name="Key">Klucz wpisu z identyfikatora oryginalnej wiadomości – ten sam mail zaimportowany drugi raz jest pomijany.</param>
/// <param name="Origin">Źródło rozpoznane z nadawcy oryginału (np. „CSIRT MON”), bez adresu e-mail.</param>
public sealed record MailEntry(string Key, string Title, DateOnly PublishedOn, string Origin, string Html, string Text);

public sealed record MailParseResult(string FileName, MailEntry? Entry, string? Error);

/// <summary>
/// Import maili .eml do wpisów własnych (np. komunikaty CSIRT MON przekazane z poczty). Zostaje sam komunikat:
/// bez nagłówków maila i przekazania (adresy), bez podpisu i stopki (klauzula RODO, dane kontaktowe) i bez obrazów –
/// ta treść idzie do Claude API. Przekazanie z Thunderbirda i Outlooka, mail w załączniku (message/rfc822), sam tekst.
/// </summary>
public static partial class MailImport
{
    /// <summary>Największy przyjmowany plik .eml.</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private static readonly HtmlParser Parser = new();

    // Polskie maile bywają w windows-1250 i ISO-8859-2 – bez stron kodowych .NET MimeKit czytałby je jako Latin-1.
    static MailImport() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Znane nadawcy komunikatów: domena → nazwa źródła (reszta – nazwa wyświetlana nadawcy).</summary>
    private static readonly (string Domain, string Name)[] KnownSenders =
    [
        ("mon.gov.pl", "CSIRT MON"),
        ("cert.pl", "CERT Polska"),
        ("csirt.gov.pl", "CSIRT GOV"),
        ("cez.gov.pl", "CSIRT CeZ"),
        ("nask.pl", "CERT Polska"),
    ];

    /// <summary>Początek podpisu albo stopki – od tego miejsca treść maila nie jest komunikatem.</summary>
    [GeneratedRegex(@"^(z wyrazami szacunku|z poważaniem|pozdrawiam|pozdrowienia|łączę wyrazy|best regards|kind regards|regards|sincerely)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Closing();

    [GeneratedRegex(@"treść przekazanej wiadomości|wiadomość przekazana|original message|forwarded message|przekazana wiadomość", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForwardMarker();

    [GeneratedRegex(@"^\s*((fwd?|fw|pd|odp|re|aw|wg)\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubjectPrefix();

    [GeneratedRegex(@"^(temat|subject|data|date|wysłane|wysłano|sent|nadawca|od|from|do|to|dw|cc|adresat)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static MailParseResult Parse(byte[] bytes, string fileName)
    {
        MimeMessage message;
        try
        {
            using var stream = new MemoryStream(bytes);
            message = MimeMessage.Load(stream);
        }
        catch (Exception e) when (e is FormatException or ParseException or IOException)
        {
            return new MailParseResult(fileName, null, "To nie jest poprawny plik e-mail (.eml).");
        }
        if (message.Headers.Count == 0 || (message.Body is null && message.From.Count == 0))
            return new MailParseResult(fileName, null, "To nie jest poprawny plik e-mail (.eml).");

        // Przekazanie „jako załącznik”: komunikatem jest wiadomość w środku.
        var outer = message;
        if (string.IsNullOrWhiteSpace(Plain(message)) && message.BodyParts.OfType<MessagePart>().FirstOrDefault()?.Message is { } inner)
            message = inner;

        var html = message.HtmlBody ?? TextToHtml(message.TextBody ?? "");
        var document = Parser.ParseDocument(html);
        var body = document.Body;
        if (body is null) return new MailParseResult(fileName, null, "Mail nie ma treści.");

        var forwarded = TakeForwardHeader(body) ?? ForwardHeaderFromText(message.TextBody);
        RemoveFooter(body);

        var clean = RichText.Sanitize(body.InnerHtml);
        var text = RichText.ToText(clean);
        if (text.Length == 0) return new MailParseResult(fileName, null, "Mail nie ma treści komunikatu.");
        if (clean.Length > RichText.MaxHtmlChars) return new MailParseResult(fileName, null, "Treść komunikatu jest za długa.");

        var title = Collapse(forwarded?.Subject is { Length: > 0 } s ? s : message.Subject ?? "");
        title = Collapse(SubjectPrefix().Replace(title, ""));
        if (title.Length == 0) title = Collapse(text.Split('\n')[0]);
        if (title.Length > 500) title = title[..500];

        var date = forwarded?.Date ?? (message.Date != DateTimeOffset.MinValue ? message.Date : outer.Date);
        var publishedOn = DateOnly.FromDateTime(WarsawClock.ToLocal(date));

        var sender = forwarded?.Sender ?? message.From.Mailboxes.FirstOrDefault();
        var origin = OriginOf(sender);

        var id = outer.Headers["X-Forwarded-Message-Id"] ?? (message != outer ? message.MessageId : null) ?? forwarded?.MessageId ?? outer.MessageId;
        var key = string.IsNullOrWhiteSpace(id)
            ? "mail-tresc:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title + "\n" + text)))[..32]
            : "mail:" + id.Trim().Trim('<', '>').ToLowerInvariant();
        if (key.Length > 900) key = key[..900];

        return new MailParseResult(fileName, new MailEntry(key, title, publishedOn, origin, clean, text), null);
    }

    private static string? Plain(MimeMessage message) => message.TextBody ?? message.HtmlBody;

    private sealed record Forwarded(string? Subject, DateTimeOffset? Date, MailboxAddress? Sender, string? MessageId);

    /// <summary>
    /// Nagłówek przekazanej wiadomości w HTML (Thunderbird: tabela moz-email-headers-table; Outlook: blok „Od: … Temat: …”).
    /// Odczytuje temat, datę i nadawcę, a z treści usuwa nagłówek i wszystko przed nim (dopisek przekazującego).
    /// </summary>
    private static Forwarded? TakeForwardHeader(IElement body)
    {
        var table = body.QuerySelector("table.moz-email-headers-table");
        IElement? header = table;
        Dictionary<string, string> fields;
        if (table is not null)
        {
            fields = table.QuerySelectorAll("tr")
                .Select(tr => (Name: tr.QuerySelector("th")?.TextContent, Value: tr.QuerySelector("td")?.TextContent))
                .Where(f => f.Name is not null && f.Value is not null)
                .GroupBy(f => Collapse(f.Name!).TrimEnd(':').Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => Collapse(g.First().Value!));
        }
        else
        {
            // Najmniejszy element z „Od:”/„From:” i „Temat:”/„Subject:” – blok nagłówka Outlooka.
            header = body.QuerySelectorAll("div, p, td").Where(e => Starts(e, "od:", "from:") && Contains(e, "temat:", "subject:"))
                .OrderBy(e => e.TextContent.Length).FirstOrDefault();
            if (header is null) return null;
            fields = HeaderFields(Lines(header));
        }

        // Wszystko przed nagłówkiem (dopisek przekazującego, „--- Treść przekazanej wiadomości ---”) i sam nagłówek.
        RemoveUpTo(body, header!);
        return ToForwarded(fields);
    }

    /// <summary>Przekazanie w samym tekście (mail bez HTML): linie „Temat: …”, „Data: …”, „Od: …” po znaczniku przekazania.</summary>
    private static Forwarded? ForwardHeaderFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => ForwardMarker().IsMatch(l));
        if (start < 0) return null;
        return ToForwarded(HeaderFields(lines.Skip(start + 1).Take(12)));
    }

    private static Dictionary<string, string> HeaderFields(IEnumerable<string> lines)
    {
        var fields = new Dictionary<string, string>();
        foreach (var line in lines.Select(l => l.Trim()))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !HeaderLine().IsMatch(line)) continue;
            fields.TryAdd(line[..colon].Trim().ToLowerInvariant(), Collapse(line[(colon + 1)..]));
        }
        return fields;
    }

    private static Forwarded? ToForwarded(Dictionary<string, string> f)
    {
        string? Get(params string[] names) => names.Select(n => f.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var subject = Get("temat", "subject");
        var dateText = Get("data", "date", "wysłane", "wysłano", "sent");
        var senderText = Get("nadawca", "od", "from");
        if (subject is null && dateText is null && senderText is null) return null;
        DateTimeOffset? date = dateText is not null && DateUtils.TryParse(dateText, out var parsed) ? parsed : null;
        MailboxAddress? sender = null;
        if (senderText is not null)
            sender = MailboxAddress.TryParse(senderText, out var box) ? box : new MailboxAddress(senderText.Split('<')[0].Trim(), "");
        return new Forwarded(subject, date, sender, null);
    }

    /// <summary>Od pierwszego bloku zaczynającego się zwrotem grzecznościowym do końca – podpis i stopka. Bez zwrotu – całość.</summary>
    private static void RemoveFooter(IElement body)
    {
        var closing = body.QuerySelectorAll("p, div, td, li, span, b, strong, i, em")
            .FirstOrDefault(e => Closing().IsMatch(TextOf(e)) && e.Children.All(c => !Closing().IsMatch(TextOf(c))));
        if (closing is null) return;
        var keep = TextOf(body);
        var before = keep.IndexOf(TextOf(closing), StringComparison.Ordinal);
        if (before <= 0 || string.IsNullOrWhiteSpace(keep[..before])) return; // zwrot na samym początku – lepiej całość niż pusty wpis
        RemoveFrom(body, closing);
    }

    /// <summary>Usuwa węzeł i wszystko przed nim (w kolejności dokumentu) w obrębie body.</summary>
    private static void RemoveUpTo(IElement body, INode node)
    {
        for (var current = node; current is not null && current != body; current = current.Parent)
        {
            while (current.PreviousSibling is { } previous) previous.RemoveFromParent();
        }
        node.RemoveFromParent();
    }

    /// <summary>Usuwa węzeł i wszystko po nim (w kolejności dokumentu) w obrębie body.</summary>
    private static void RemoveFrom(IElement body, INode node)
    {
        for (var current = node; current is not null && current != body; current = current.Parent)
        {
            while (current.NextSibling is { } next) next.RemoveFromParent();
        }
        node.RemoveFromParent();
    }

    private static string OriginOf(MailboxAddress? sender)
    {
        if (sender is null) return "";
        var domain = sender.Address.Split('@').LastOrDefault()?.ToLowerInvariant() ?? "";
        foreach (var (known, name) in KnownSenders)
            if (domain == known || domain.EndsWith("." + known, StringComparison.Ordinal)) return name;
        var display = Collapse(sender.Name ?? "");
        return display.Length > 0 ? (display.Length > 200 ? display[..200] : display) : domain;
    }

    private static string TextToHtml(string text)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(paragraphs.Select(p => "<p>" + WebUtility.HtmlEncode(p.Trim()).Replace("\n", "<br>") + "</p>"));
    }

    private static IEnumerable<string> Lines(IElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Descendants())
        {
            if (node is IText t) sb.Append(t.Data);
            else if (node is IElement { LocalName: "br" or "p" or "div" or "tr" }) sb.Append('\n');
        }
        return sb.ToString().Split('\n');
    }

    /// <summary>Tekst elementu z odstępem w miejscu &lt;br&gt; i bloków („Pozdrawiam&lt;br&gt;Jan” → „Pozdrawiam Jan”).</summary>
    private static string TextOf(IElement element) => Collapse(string.Join(" ", Lines(element)));

    private static bool Starts(IElement e, params string[] prefixes) =>
        prefixes.Any(p => Collapse(e.TextContent).StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(IElement e, params string[] parts) =>
        parts.Any(p => e.TextContent.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static string Collapse(string text) => Spaces().Replace(HtmlText.Normalize(text).Replace('\n', ' '), " ").Trim();
}
