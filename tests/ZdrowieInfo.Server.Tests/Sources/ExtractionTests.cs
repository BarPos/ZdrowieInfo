using ZdrowieInfo.Server.Sources;
using ZdrowieInfo.Server.Sources.Extraction;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Sources;

/// <summary>Selektory z DefaultSources.json sprawdzone na prawdziwych, zapisanych stronach.</summary>
public class ExtractionTests(ITestOutputHelper output)
{
    public static TheoryData<string> ListSources() => new(DefaultSource.Load().Where(s => s.Method == SourceMethod.List).Select(s => s.Slug));
    public static TheoryData<string> PageSources() => new(DefaultSource.Load().Where(s => s.Method == SourceMethod.Page).Select(s => s.Slug));

    private static DefaultSource Get(string slug) => DefaultSource.Load().Single(s => s.Slug == slug);

    /// <summary>Pierwsza strona listy: a.html albo a.json (lista z API).</summary>
    private static string ListFixture(DefaultSource s) => Fixtures.Read(s.Slug, s.Selectors.Format == ListFormat.Json ? "a.json" : "a.html");

    [Fact]
    public void There_are_23_sources_with_fixtures()
    {
        var sources = DefaultSource.Load();
        Assert.Equal(23, sources.Count);
        Assert.Equal(sources.Count, sources.Select(s => s.Slug).Distinct().Count());
        Assert.All(sources, s => Assert.True(Fixtures.Exists(s.Slug, "a.html") || Fixtures.Exists(s.Slug, "a.json"), s.Slug));
    }

    [Theory]
    [MemberData(nameof(ListSources))]
    public void List_selectors_find_entries(string slug)
    {
        var source = Get(slug);
        var result = SourceExtractor.ExtractList(ListFixture(source), source.Url, source.Selectors);

        foreach (var item in result.Items)
            output.WriteLine($"{item.PublishedOn:yyyy-MM-dd} | {item.Title} | {item.Url}");

        Assert.True(result.Items.Count >= 3, $"{slug}: {result.Items.Count} wpisów");
        Assert.All(result.Items, i =>
        {
            Assert.StartsWith("https://", i.Url);
            Assert.False(string.IsNullOrWhiteSpace(i.Title));
            Assert.DoesNotContain("\n", i.Title);
        });
        if (source.Selectors.Date is not null)
            Assert.All(result.Items, i => Assert.NotNull(i.PublishedOn));
        Assert.Equal(0, result.Skipped);
    }

    [Theory]
    [InlineData("nfz-walidacje", "nfz-walidacje", "https://www.nfz.gov.pl/dla-swiadczeniodawcy/sprawozdawczosc-elektroniczna/walidacje-i-weryfikacje/2,strona.html", "3,strona.html")]
    [InlineData("nfz-mazowsze", "nfz-mazowsze", "https://nfz-warszawa.pl/dla-swiadczeniodawcow/aktualnosci/2,strona.html", "3,strona.html")]
    [InlineData("csirt-cez", "csirt-cez", "https://csirt.cez.gov.pl/pl/wszystkie-aktualnosci?page=1", "?page=2")]
    [InlineData("cez-aktualnosci", "cez-aktualnosci", "https://www.cez.gov.pl/pl/page/o-nas/aktualnosci?page=1", "?page=2")]
    [InlineData("mz-komunikaty", "mz-komunikaty", "https://www.gov.pl/web/zdrowie/komunikaty1?page=2&size=10", "page=3")]
    [InlineData("nfz-aktualnosci", "nfz-archiwum", "https://www.nfz.gov.pl/aktualnosci/aktualnosci-centrali/2,strona.html", "3,strona.html")]
    public void Next_page_selector_leads_through_the_list(string slug, string fixture, string expectedPage2, string page3Fragment)
    {
        var source = Get(slug);
        var firstUrl = fixture == "nfz-archiwum" ? source.Selectors.OverflowUrl! : source.Url;
        var first = SourceExtractor.ExtractList(Fixtures.Read(fixture, "a.html"), firstUrl, source.Selectors);
        Assert.Equal(expectedPage2, first.NextPageUrl);

        var second = SourceExtractor.ExtractList(Fixtures.Read(fixture, "page2.html"), expectedPage2, source.Selectors);
        Assert.True(second.Items.Count >= 3, $"{slug}: {second.Items.Count} wpisów na 2. stronie");
        Assert.Contains(page3Fragment, second.NextPageUrl);
        // Druga strona to inne (starsze) wpisy niż pierwsza. Wspólny może być jeden: wyróżniony najnowszy artykuł
        // (CSIRT pokazuje go na górze każdej strony) albo wpis, który przesunął się między pobraniami.
        var shared = second.Items.Where(i => first.Items.Any(f => f.Key == i.Key)).ToList();
        Assert.True(shared.Count <= 1);
        var oldestFirst = first.Items.Where(i => i.PublishedOn is not null).Min(i => i.PublishedOn);
        var newestSecond = second.Items.Except(shared).Where(i => i.PublishedOn is not null).Max(i => i.PublishedOn);
        Assert.True(newestSecond <= oldestFirst, $"{slug}: {newestSecond} > {oldestFirst}");
    }

    [Theory]
    [InlineData("nfz-aktualnosci")]
    [InlineData("nfz-slowniki")]
    [InlineData("nfz-di-komunikaty")]
    public void Nfz_blocks_without_pages_have_paginated_archive_as_overflow(string slug)
    {
        var source = Get(slug);
        Assert.Null(SourceExtractor.ExtractList(Fixtures.Read(slug, "a.html"), source.Url, source.Selectors).NextPageUrl);
        Assert.Equal("https://www.nfz.gov.pl/aktualnosci/aktualnosci-centrali/", source.Selectors.OverflowUrl);
        var archive = SourceExtractor.ExtractList(Fixtures.Read("nfz-archiwum", "a.html"), source.Selectors.OverflowUrl!, source.Selectors);
        // Wszystkie wpisy archiwum, także z karuzeli „Ważne!” (9059, 9055), której nie ma w .news. Karuzela nie ma dat –
        // w trybie archiwum takie wpisy są brane (nie da się ich odrzucić po dacie), a granicę wyznaczają wpisy datowane.
        Assert.Equal(4, archive.Items.Count(i => i.PublishedOn is not null));
        var ids = archive.Items.Select(i => i.Url[(i.Url.LastIndexOf(',') + 1)..].Replace(".html", "")).Order().ToList();
        Assert.Equal(["9034", "9055", "9056", "9057", "9058", "9059"], ids);
    }

    [Fact]
    public void Ezdrowie_json_list_gives_40_articles_with_portal_links()
    {
        var source = Get("ezdrowie-podmioty");
        var result = SourceExtractor.ExtractList(Fixtures.Read("ezdrowie-podmioty", "a.json"), source.Url, source.Selectors);
        Assert.Equal(40, result.Items.Count);
        var first = result.Items[0];
        Assert.Equal("https://ezdrowie.gov.pl/portal/artykul/18-listopada-przerwa-w-dostepie-do-platformy-uslug-inteligentnych", first.Url);
        Assert.Equal(new DateOnly(2026, 11, 17), first.PublishedOn);
        Assert.Contains("PUI", first.Summary);
        // Te same klucze co linki ze strony HTML (stan bazowy po zmianie formatu nie dubluje wpisów).
        var html = SourceExtractor.ExtractList(Fixtures.Read("ezdrowie-podmioty", "a.html"), "https://ezdrowie.gov.pl/portal/home/dla-podmiotow-leczniczych",
            new SourceSelectors { Item = "li.article-list-element", Title = ".articleListBigBoxTitle", Link = "a.showMoreText" });
        Assert.All(html.Items, h => Assert.Contains(result.Items, j => j.Key == h.Key));
    }

    [Fact]
    public void Invalid_json_is_reported_as_selector_problem() =>
        Assert.Throws<SelectorException>(() => SourceExtractor.ExtractList("<html>błąd</html>", "https://x.pl", new SourceSelectors { Format = ListFormat.Json, Item = "a" }));

    [Fact]
    public void Csirt_takes_main_list_articles()
    {
        var source = Get("csirt-cez");
        var items = SourceExtractor.ExtractList(Fixtures.Read("csirt-cez", "a.html"), source.Url, source.Selectors).Items;
        Assert.All(items, i => Assert.Contains("/pl/aktualnosci/", i.Url));
        Assert.Contains(items, i => i.Title.Contains("cyberbezpieczeństwa w szpitalach"));
    }

    [Fact]
    public void Nfz_entries_have_titles_dates_and_summaries()
    {
        var source = Get("nfz-aktualnosci");
        var first = SourceExtractor.ExtractList(Fixtures.Read("nfz-aktualnosci", "a.html"), source.Url, source.Selectors).Items[0];
        Assert.StartsWith("https://www.nfz.gov.pl/aktualnosci/aktualnosci-centrali/", first.Url);
        Assert.Equal(new DateOnly(2026, 11, 16), first.PublishedOn);
        Assert.NotNull(first.Summary);
        Assert.Equal("nfz.gov.pl/aktualnosci/aktualnosci-centrali/" + first.Url.Split("aktualnosci-centrali/")[1], first.Key);
    }

    [Theory]
    [MemberData(nameof(PageSources))]
    public void Page_selectors_find_content(string slug)
    {
        var source = Get(slug);
        var text = SourceExtractor.ExtractPageText(Fixtures.Read(slug, "a.html"), source.Url, source.Selectors);
        output.WriteLine(text ?? "(brak)");

        Assert.NotNull(text);
        Assert.True(text.Length > 200, $"{slug}: {text.Length} znaków");
        Assert.DoesNotContain("Drukuj", text);
        Assert.DoesNotContain("cookies", text);
    }

    [Theory]
    [MemberData(nameof(PageSources))]
    public void Page_text_is_identical_between_two_downloads(string slug)
    {
        Assert.SkipUnless(Fixtures.Exists(slug, "b.html"), "brak drugiego pobrania");
        var source = Get(slug);
        var a = SourceExtractor.ExtractPageText(Fixtures.Read(slug, "a.html"), source.Url, source.Selectors);
        var b = SourceExtractor.ExtractPageText(Fixtures.Read(slug, "b.html"), source.Url, source.Selectors);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Page_text_includes_file_links()
    {
        var source = Get("nfz-swiad");
        var text = SourceExtractor.ExtractPageText(Fixtures.Read("nfz-swiad", "a.html"), source.Url, source.Selectors)!;
        Assert.Contains("Komunikat SWIAD (wersja 8.8.3) (https://www.nfz.gov.pl/download/", text);
    }

    [Theory]
    [InlineData("nfz-aktualnosci", "zaplanowanymi pracami serwisowymi")]
    [InlineData("nfz-mazowsze", "rozstrzygnięte postępowanie")]
    [InlineData("csirt-cez", "ankiet")]
    [InlineData("cez-aktualnosci", "ankiet")]
    [InlineData("mz-komunikaty", "Komisji Ekonomicznej")]
    [InlineData("ezdrowie-podmioty", "Platforma Usług Inteligentnych (PUI) będzie niedostępna")]
    public void Detail_pages_give_article_text(string slug, string expected)
    {
        var source = Get(slug);
        var text = SourceExtractor.ExtractDetailText(Fixtures.Read(slug, "detail.html"), source.Url, source.Selectors);
        output.WriteLine(text ?? "(brak)");
        Assert.NotNull(text);
        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
        Assert.True(text.Length < 20_000, $"{slug}: {text.Length} znaków – selektor łapie za dużo");
        Assert.DoesNotContain("Infolinia", text);
    }

    [Fact]
    public void Removing_first_entry_shows_exactly_one_new_entry()
    {
        var source = Get("nfz-aktualnosci");
        var html = Fixtures.Read("nfz-aktualnosci", "a.html");
        var document = SourceExtractor.Parse(html, source.Url);
        document.QuerySelector(".news-list .news")!.Remove();

        var before = SourceExtractor.ExtractList(document.DocumentElement.OuterHtml, source.Url, source.Selectors).Items.Select(i => i.Key).ToHashSet();
        var after = SourceExtractor.ExtractList(html, source.Url, source.Selectors).Items;

        var added = after.Where(i => !before.Contains(i.Key)).ToList();
        Assert.Single(added);
        Assert.Contains("CZPR", added[0].Title);
    }

    [Fact]
    public void Invalid_selector_is_reported_clearly()
    {
        var e = Assert.Throws<SelectorException>(() =>
            SourceExtractor.ExtractList("<html></html>", "https://example.pl", new SourceSelectors { Item = "div[[" }));
        Assert.Contains("div[[", e.Message);
    }
}
