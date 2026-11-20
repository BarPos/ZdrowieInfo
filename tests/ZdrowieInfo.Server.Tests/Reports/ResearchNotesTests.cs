using ZdrowieInfo.Server.Reports;

namespace ZdrowieInfo.Server.Tests.Reports;

/// <summary>Notatki ze sprawdzania podatności składane jak „Ustalenia”: nagłówek podatności, punkty z etykietą, akapity, linki.</summary>
public class ResearchNotesTests
{
    // Fragment prawdziwych notatek z raportu 19.11.2026 (układ, w jakim model je zwrócił).
    private const string Notes = """
        Wpisy CSIRT CeZ nie podają numerów CVE. Dopasowanie poniżej opiera się na zbieżności daty.
        NGINX Plus i NGINX Open Source – CVE-2026-123007, CVE-2026-123633
        – Zakres: NHS England Digital opisuje pięć krytycznych podatności w NGINX.
        – Poprawki: 1.30.7 (stable) i 1.31.9 (mainline), wydane 18 listopada 2026.
        – Komunikat producenta: F5 K000163926 (dotyczy CVE-2026-123007), https://my.f5.com/manage/s/article/K000163926.
        **F5 – 6 krytycznych podatności (prawdopodobnie BIG-IP)**
        - Identyfikatorów CVE nie ustalono. Wyniki wyszukiwania ich nie podają.
        • CVSS: CSIRT CeZ podaje 9,3.
        """;

    [Fact]
    public void Lines_before_points_are_headings_and_points_get_labels()
    {
        var blocks = ResearchNotes.Parse(Notes);

        Assert.Equal(
            [ResearchBlockKind.Paragraph, ResearchBlockKind.Heading, ResearchBlockKind.Points, ResearchBlockKind.Heading, ResearchBlockKind.Points],
            blocks.Select(b => b.Kind));
        Assert.Equal("NGINX Plus i NGINX Open Source – CVE-2026-123007, CVE-2026-123633", blocks[1].Text);
        Assert.Equal(["Zakres", "Poprawki", "Komunikat producenta"], blocks[2].Points.Select(p => p.Label));
        Assert.Equal("1.30.7 (stable) i 1.31.9 (mainline), wydane 18 listopada 2026.", blocks[2].Points[1].Text);
        Assert.Equal("F5 – 6 krytycznych podatności (prawdopodobnie BIG-IP)", blocks[3].Text); // bez **
        Assert.Null(blocks[4].Points[0].Label); // zdanie bez etykiety (myślnik „-” też jest punktem)
        Assert.Equal("CVSS", blocks[4].Points[1].Label); // „•” też
    }

    [Fact]
    public void Urls_are_split_out_without_sentence_punctuation()
    {
        var segments = ResearchNotes.Segments("Komunikat F5 (https://my.f5.com/a?b=1&c=2), zob. też https://cert.pl/x.");

        Assert.Equal(
            [("Komunikat F5 (", false), ("https://my.f5.com/a?b=1&c=2", true), ("), zob. też ", false), ("https://cert.pl/x", true), (".", false)],
            segments.Select(s => (s.Text, s.IsUrl)));
    }
}
