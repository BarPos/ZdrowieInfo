using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Export;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Export;

public class LatexExportTests(PostgresFixture postgres)
{
    private static bool LatexAvailable => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Any(p => File.Exists(Path.Combine(p, "latexmk")) && File.Exists(Path.Combine(p, "xelatex")));

    private static Report SampleReport(string title = "Przerwa w PUI – 18.11.2026") => new()
    {
        Date = new DateOnly(2026, 11, 18),
        Title = title,
        Summary = "Dziś 20:00–24:00 przerwa w PUI. Koszt: 100% & więcej_niż #1 {ważne} ~ ^ $.",
        Producer = "claude",
        Model = "claude-sonnet-5-5",
        Status = ReportStatus.Ready,
        SourcesState = [new SourceStateEntry(1, "ezdrowie.gov.pl", "https://ezdrowie.gov.pl", true, true, null, null, null, false, [])],
        Body = new ReportDocument { Ignored = [new IgnoredDto { ItemId = "9", Reason = "dotyczy aptek" }] },
        Findings =
        [
            new ReportFinding
            {
                Importance = Importance.Critical, Category = FindingCategory.Outage, Headline = "PUI niedostępna dziś 20–24", Body = "Przerwa techniczna.",
                Action = "Uprzedzić rejestrację do 18:00.",
                Citations = [new FindingCitation { Title = "18 listopada przerwa w PUI", Url = "https://ezdrowie.gov.pl/portal/artykul/pui?a=1&b=2#x", Quote = "PUI będzie niedostępna", Verified = true, PublishedOn = new DateOnly(2026, 11, 17) }],
            },
        ],
    };

    private static LatexReportRenderer Renderer() =>
        new(Options.Create(new ExportOptions()), new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 5, 0, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData(@"\input{/etc/passwd}", @"\textbackslash{}input\{/etc/passwd\}")]
    [InlineData("100% & $5 #1 a_b ~ ^", @"100\% \& \$5 \#1 a\_b \textasciitilde{} \textasciicircum{}")]
    [InlineData("\"cytat\"", @"\textquotedbl{}cytat\textquotedbl{}")]
    [InlineData("linia1\nlinia2", "linia1\\par\nlinia2")]
    public void Escape_turns_special_characters_into_text(string input, string expected) =>
        Assert.Equal(expected, LatexText.Escape(input));

    [Fact]
    public void Url_escape_keeps_link_working_inside_footnote() =>
        Assert.Equal(@"https://x.pl/a?b=1\%20\#c%7Bd%7D", LatexText.Url("https://x.pl/a?b=1%20#c{d}"));

    [Fact]
    public void Template_renders_numbered_findings_with_footnotes_and_escaped_text()
    {
        var tex = Renderer().Render(ReportView.Build(SampleReport(), [new ReportView.Entry(9, "Apteki", "https://x.pl/9", "MZ", null)]));

        Assert.Contains(@"\section{Podsumowanie}", tex);
        Assert.Contains(@"100\% \& więcej\_niż \#1 \{ważne\}", tex);
        Assert.Contains(@"\textbf{2.1.~PUI niedostępna dziś 20–24}", tex);
        Assert.Contains(@"\textbf{Do zrobienia:} Uprzedzić rejestrację do 18:00.", tex);
        Assert.Contains(@"\footnote{18 listopada przerwa w PUI, 17.11.2026. \url{https://ezdrowie.gov.pl/portal/artykul/pui?a=1&b=2\#x}", tex);
        Assert.Contains(@"\color{critical}", tex);
        Assert.Contains("dotyczy aptek", tex);
        Assert.DoesNotContain("{{", tex);
    }

    [Fact]
    public async Task Compiles_pdf_and_ignores_latex_commands_in_report_text()
    {
        Assert.SkipUnless(LatexAvailable, "brak latexmk/xelatex w PATH");
        var dir = Directory.CreateTempSubdirectory("zdrowieinfo-latex-").FullName;
        var marker = Path.Combine(dir, "wstrzykniete");
        var compiler = new PdfCompiler(Options.Create(new ExportOptions()), Options.Create(new ZdrowieInfoOptions { DataDirectory = dir, PublicUrl = "https://x" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfCompiler>.Instance);

        var hostile = SampleReport($@"\immediate\write18{{touch {marker}}} \input{{/etc/passwd}}");
        hostile.Findings[0].Headline = "Nagłówek\nw dwóch liniach"; // \par w \textbf przerwałby kompilację
        hostile.Findings[0].Citations[0].Title = "Tytuł\nz nową linią";
        // Notatki z internetu też idą przez escape (treść stron i modelu), a adresy źródeł przez \href.
        hostile.Research = new VulnerabilityResearch($"Uwaga \\textbf{{x}} % ogólna.\nFortiOS (CVE-2026-1) {{x}}\n– Poprawka: 7.4.3 \\input{{/etc/passwd}} & 50% #1 _x_\n– Komunikat producenta: https://my.f5.com/manage/s/article/K000163926_bardzo_dlugi_adres_bez_spacji_ktory_musi_sie_zlamac?a=1&b=%20#x.", [new ResearchSource("Fortinet {PSIRT}", "https://www.fortiguard.com/psirt?a=1&b=%20#x")], 1);
        var result = await compiler.CompileAsync(Renderer().Render(ReportView.Build(hostile, [])));

        Assert.True(result.Success, result.Error);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(result.Pdf!, 0, 4));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Report_pdf_is_for_admins_and_generated_on_demand()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", "Platforma Usług Inteligentnych (PUI) będzie niedostępna w nocy.");
        await app.Services.GetRequiredService<DailyReportService>().GenerateAsync(new DateOnly(2026, 11, 18));

        var anonymous = app.CreateBrowser();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/eksport/raport-2026-11-18.pdf")).StatusCode);

        var reader = await app.SignedInBrowserAsync(UserRole.User, "czytelnik");
        Assert.Equal(HttpStatusCode.Redirect, (await reader.GetAsync("/eksport/raport-2026-11-18.pdf")).StatusCode); // → odmowa dostępu
        Assert.Contains("/Account/AccessDenied", (await reader.GetAsync("/eksport/raport-2026-11-18.pdf")).Headers.Location!.ToString());

        var browser = await app.SignedInBrowserAsync();
        // Pobieranie .tex usunięte – plik powstaje tylko do kompilacji PDF.
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/eksport/raport-2026-11-18.tex")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/eksport/raport-2026-11-17.pdf")).StatusCode);

        var pdf = await browser.GetAsync("/eksport/raport-2026-11-18.pdf");
        if (LatexAvailable)
        {
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
            // Wbudowana przeglądarka PDF nie działa z CSP stron (object-src 'none') ani z X-Frame-Options.
            Assert.False(pdf.Headers.Contains("Content-Security-Policy"));
            Assert.False(pdf.Headers.Contains("X-Frame-Options"));
            Assert.Equal("nosniff", pdf.Headers.GetValues("X-Content-Type-Options").Single());
        }
        else
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pdf.StatusCode);
        }
    }

    [Fact]
    public async Task Report_page_links_pdf_and_shows_document_sections()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", "Platforma Usług Inteligentnych (PUI) będzie niedostępna w nocy.");
        await app.Services.GetRequiredService<DailyReportService>().GenerateAsync(new DateOnly(2026, 11, 18));
        var browser = await app.SignedInBrowserAsync();

        var html = await (await browser.GetAsync("/raporty/2026-11-18")).Content.ReadAsStringAsync();
        Assert.Contains("eksport/raport-2026-11-18.pdf", html);
        var text = await browser.TextAsync("/raporty/2026-11-18");
        Assert.Contains("1. Podsumowanie", text);
        Assert.Contains("2.1.", text);
        Assert.Contains("Przypisy", text);
    }

    [Fact]
    public async Task Pdf_has_the_same_links_as_the_page_and_importance_above_category()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var cited = await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", "PUI będzie niedostępna.");
        var skipped = await app.AddItemAsync("mz-komunikaty", "Apteki", "https://www.gov.pl/web/zdrowie/apteki?a=1&b=2", "Dla aptek.");
        var date = new DateOnly(2026, 11, 18);
        await using (var db = await app.DbAsync())
        {
            db.Reports.Add(new Report
            {
                Date = date, Title = "Raport 18.11", Summary = "Podsumowanie.", Producer = "claude", Status = ReportStatus.Ready,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, InputItemIds = [cited.Id, skipped.Id],
                Body = new ReportDocument { Ignored = [new IgnoredDto { ItemId = skipped.Id.ToString(), Reason = "dotyczy aptek" }] },
                SourcesState = [new SourceStateEntry(1, "NFZ – Komunikaty", "https://www.nfz.gov.pl/komunikaty/", true, false, "HTTP 503", null, null, true, [])],
                Research = new VulnerabilityResearch("FortiOS\n– Poprawka: 7.4.3 (100% pewne & #1)\n– Komunikat producenta: https://my.f5.com/manage/s/article/K000163926.", [new ResearchSource("Fortinet PSIRT", "https://www.fortiguard.com/psirt/FG-IR-24-015?a=1&b=2")], 2),
                Findings =
                [
                    new ReportFinding
                    {
                        Importance = Importance.Critical, Category = FindingCategory.Outage, Headline = "PUI niedostępna", Body = "Przerwa.",
                        Citations = [new FindingCitation { SourceItemId = cited.Id, Title = cited.Title, Url = cited.Url, Quote = "PUI będzie niedostępna.", Verified = true }],
                    },
                    new ReportFinding { Importance = Importance.Low, Category = FindingCategory.Other, Headline = "Drobiazg", Body = "Informacja.", Order = 1 },
                ],
            });
            await db.SaveChangesAsync();
        }

        // Strona: wszystkie linki zewnętrzne dokumentu (przypisy, pominięte wpisy, źródła z uwagami, wpisy źródłowe).
        var browser = await app.SignedInBrowserAsync();
        var page = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await (await browser.GetAsync("/raporty/2026-11-18")).Content.ReadAsStringAsync());
        var pageLinks = page.QuerySelectorAll("article.doc a[href^='http']").Select(a => a.GetAttribute("href")!).Order().ToList();

        // PDF: cele \url i \href z dokumentu LaTeX (to samo, co trafia do adnotacji PDF).
        string tex;
        await using (var db = await app.DbAsync())
            tex = app.Services.GetRequiredService<LatexReportRenderer>().Render((await ReportView.LoadAsync(db, date))!);
        var texLinks = System.Text.RegularExpressions.Regex.Matches(tex, @"\\(?:url|href)\{([^}]*)\}")
            .Select(m => m.Groups[1].Value.Replace(@"\%", "%").Replace(@"\#", "#")).Order().ToList();

        Assert.Equal(7, pageLinks.Count); // przypis, pominięty wpis, źródło z uwagą, adres w notatkach i źródło sprawdzania podatności, 2 wpisy źródłowe
        Assert.Contains("https://my.f5.com/manage/s/article/K000163926", pageLinks); // bez kropki kończącej zdanie
        Assert.Equal(pageLinks, texLinks);

        // Ważność nad kategorią przy każdym punkcie (bez nagłówków grup).
        var labels = page.QuerySelectorAll(".doc-finding").Select(f => (f.QuerySelector(".doc-imp")?.TextContent, f.QuerySelector(".doc-cat")?.TextContent)).ToList();
        Assert.Equal([("krytyczna", "awaria"), ("niska", "inne")], labels);
        Assert.Empty(page.QuerySelectorAll(".doc-group"));
        Assert.True(tex.IndexOf(@"\MakeUppercase{ krytyczna }", StringComparison.Ordinal) < tex.IndexOf(@"\MakeUppercase{ awaria }", StringComparison.Ordinal));
        Assert.True(tex.IndexOf(@"\MakeUppercase{ niska }", StringComparison.Ordinal) > 0);
    }
}
