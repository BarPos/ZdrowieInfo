using ZdrowieInfo.Server.Reports;

namespace ZdrowieInfo.Server.Tests.Reports;

public class ReportValidatorTests
{
    private const string Text = "Informujemy, że w związku z zaplanowanymi pracami serwisowymi w dniu 12.11.2026r. od godziny 17.00 do 13.11.2026 do godziny 06.00 – mogą wystąpić utrudnienia w dostępie do systemów informatycznych Centrali NFZ.";

    private static readonly ReportInputItem Item = new("10", [10, 11], ["NFZ – Aktualności"], "Prace serwisowe DILO", "https://www.nfz.gov.pl/a,1.html",
        new DateOnly(2026, 11, 3), "nowy wpis", null, Text, false);

    private static ReportDocument Doc(string quote, string itemId = "10", bool ignoreSecond = true) => new()
    {
        Title = "Raport",
        Summary = "Podsumowanie.",
        Findings = [new FindingDto { Importance = "wysoka", Category = "awaria", Headline = "Przerwa", Body = "Treść.", Citations = [new CitationDto { ItemId = itemId, Quote = quote }] }],
    };

    [Theory]
    [InlineData("w związku z zaplanowanymi pracami serwisowymi w dniu 12.11.2026r.")]
    [InlineData("W ZWIĄZKU Z   zaplanowanymi\npracami serwisowymi")] // wielkość liter i białe znaki
    [InlineData("13.11.2026 do godziny 06.00 - mogą wystąpić utrudnienia")] // myślnik zwykły zamiast półpauzy
    [InlineData("Informujemy, że … mogą wystąpić utrudnienia w dostępie")] // wielokropek: części w kolejności
    [InlineData("Prace serwisowe DILO")] // tytuł też można cytować
    public void Verbatim_quotes_pass(string quote) =>
        Assert.True(ReportValidator.Validate(Doc(quote), [Item], requireCoverage: true).IsValid);

    [Theory]
    [InlineData("w związku z planowanymi pracami serwisowymi")] // parafraza
    [InlineData("mogą wystąpić utrudnienia … Informujemy, że")] // zła kolejność
    [InlineData("od godziny 18.00 do 13.11.2026")] // zmieniona liczba
    [InlineData("NFZ")] // za krótki
    public void Altered_quotes_fail(string quote)
    {
        var result = ReportValidator.Validate(Doc(quote), [Item], requireCoverage: true);
        Assert.False(result.IsValid);
        Assert.False(Assert.Single(result.Citations).Verified);
    }

    [Fact]
    public void Unknown_item_and_missing_coverage_are_errors()
    {
        var second = Item with { Id = "20", Title = "Inny wpis", Url = "https://x.pl/2" };
        var result = ReportValidator.Validate(Doc("zaplanowanymi pracami serwisowymi", itemId: "99"), [Item, second], requireCoverage: true);
        Assert.Contains(result.Errors, e => e.Contains("nieistniejący wpis „99”"));
        Assert.Contains(result.Errors, e => e.Contains("Wpis 10") && e.Contains("ani w żadnym punkcie"));
        Assert.Contains(result.Errors, e => e.Contains("Wpis 20"));
    }

    [Fact]
    public void Ignored_items_count_as_covered()
    {
        var second = Item with { Id = "20", Title = "Temat dla aptek", Url = "https://x.pl/2" };
        var doc = Doc("zaplanowanymi pracami serwisowymi") with { Ignored = [new IgnoredDto { ItemId = "20", Reason = "dotyczy aptek" }] };
        Assert.True(ReportValidator.Validate(doc, [Item, second], requireCoverage: true).IsValid);
    }

    [Fact]
    public void Finding_without_citation_is_error()
    {
        var doc = Doc("x") with { Findings = [new FindingDto { Importance = "niska", Category = "inne", Headline = "H", Body = "B" }] };
        Assert.Contains(ReportValidator.Validate(doc, [], requireCoverage: false).Errors, e => e.Contains("brak cytatu"));
    }

    [Fact]
    public void External_url_citation_is_unverified_not_error()
    {
        var doc = Doc("x") with
        {
            Findings = [new FindingDto { Importance = "niska", Category = "inne", Headline = "H", Body = "B", Citations = [new CitationDto { Url = "https://inna-strona.pl/x", Quote = "Cokolwiek zewnętrznego" }] }],
        };
        var result = ReportValidator.Validate(doc, [Item], requireCoverage: false, allowUrlCitations: true);
        Assert.True(result.IsValid);
        Assert.False(Assert.Single(result.Citations).Verified);
    }

    [Fact]
    public void Wrong_vocabulary_is_error()
    {
        var doc = Doc("zaplanowanymi pracami serwisowymi") with
        {
            Findings = [new FindingDto { Importance = "pilna", Category = "IT", Headline = "H", Body = "B", Citations = [new CitationDto { ItemId = "10", Quote = "zaplanowanymi pracami serwisowymi" }] }],
        };
        var errors = ReportValidator.Validate(doc, [Item], requireCoverage: true).Errors;
        Assert.Contains(errors, e => e.Contains("ważność „pilna”"));
        Assert.Contains(errors, e => e.Contains("kategoria „IT”"));
    }
}
