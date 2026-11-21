using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Reports;

/// <summary>Szacunkowy koszt AI wpisu: udział w zużyciu raportów, wyszukiwania tylko na sprawdzane wpisy.</summary>
public class EntryCostsTests(PostgresFixture postgres)
{
    private const string Sonnet = "claude-sonnet-5-5";

    private static async Task AddReportAsync(AppFactory app, DateOnly date, string? model, AiUsage? usage, List<long> items, VulnerabilityResearch? research = null)
    {
        await using var db = await app.DbAsync();
        db.Reports.Add(new Report
        {
            Date = date, Title = "Raport", Summary = "S.", Producer = "claude", Model = model, Status = ReportStatus.Ready,
            AiUsage = usage, InputItemIds = items, Research = research, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public void Pricing_matches_the_model_list_and_unknown_models_have_no_price()
    {
        var usage = new AiUsage(1_000_000, 1_000_000, 1_000_000, 1_000_000, 1, 3);
        Assert.Equal(2m + 10m + 0.10m + 2.50m + 0.03m, AiPricing.Cost(usage, "claude-sonnet-5-5"));
        Assert.Equal(2m + 10m + 0.20m + 2.50m + 0.03m, AiPricing.Cost(usage, "claude-sonnet-5")); // Sonnet 5: inny odczyt cache
        Assert.Equal(4m + 20m + 0.20m + 5m + 0.03m, AiPricing.Cost(usage, "\"claude-opus-5-5\""));
        Assert.Null(AiPricing.Cost(usage, "gpt-x"));
        Assert.Null(AiPricing.Cost(usage, null));
    }

    [Fact]
    public async Task Shares_follow_text_length_and_sum_back_to_the_report()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var longItem = await app.AddItemAsync("nfz-aktualnosci", "Długi", "https://www.nfz.gov.pl/a,1.html", new string('x', 3000));
        var shortItem = await app.AddItemAsync("nfz-aktualnosci", "Krótki", "https://www.nfz.gov.pl/b,2.html", new string('y', 300));
        var other = await app.AddItemAsync("nfz-aktualnosci", "Bez raportu", "https://www.nfz.gov.pl/c,3.html", "z");
        var usage = new AiUsage(8000, 1200, 6000, 0, 1);
        await AddReportAsync(app, new DateOnly(2026, 11, 18), Sonnet, usage, [longItem.Id, shortItem.Id]);
        await AddReportAsync(app, new DateOnly(2026, 11, 19), "sample", new AiUsage(0, 0, 0, 0, 0), [other.Id]); // generator przykładowy

        await using var db = await app.DbAsync();
        var costs = await EntryCosts.EstimateAsync(db, [longItem.Id, shortItem.Id, other.Id]);

        Assert.False(costs.ContainsKey(other.Id)); // tylko raport bez tokenów – „—”
        Assert.True(costs[longItem.Id].InputTokens > costs[shortItem.Id].InputTokens * 5);
        Assert.InRange(costs[longItem.Id].InputTokens + costs[shortItem.Id].InputTokens, 13_999, 14_001);
        Assert.InRange(costs[longItem.Id].OutputTokens + costs[shortItem.Id].OutputTokens, 1199, 1201);
        Assert.Equal(AiPricing.Cost(usage, Sonnet)!.Value, costs[longItem.Id].CostUsd!.Value + costs[shortItem.Id].CostUsd!.Value, 10);
    }

    [Fact]
    public async Task Research_and_searches_go_only_to_researched_entries_and_reports_add_up()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var vulnerable = await app.AddItemAsync("csirt-cez", "Podatność w FortiOS", "https://csirt.cez.gov.pl/a", "Krytyczna podatność CVE-2026-1.");
        var outage = await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w P1", "https://ezdrowie.gov.pl/b", "Przerwa techniczna w nocy.");
        var analysis = new AiUsage(5000, 800, 0, 0, 1);
        var research = new AiUsage(3000, 400, 0, 0, 1, 2);
        // Raport dzienny ze sprawdzaniem (zużycie razem, jak zapisuje DailyReportService) i drugi – okresowy, bez sprawdzania.
        await AddReportAsync(app, new DateOnly(2026, 11, 18), Sonnet,
            new AiUsage(8000, 1200, 0, 0, 2, 2), [vulnerable.Id, outage.Id],
            new VulnerabilityResearch("notatki", [], 2, Usage: research, ItemIds: [vulnerable.Id]));
        await AddReportAsync(app, new DateOnly(2026, 11, 19), Sonnet, analysis, [outage.Id]);

        await using var db = await app.DbAsync();
        var costs = await EntryCosts.EstimateAsync(db, [vulnerable.Id, outage.Id]);

        Assert.Equal(0, costs[outage.Id].WebSearches);
        Assert.Equal(2, costs[vulnerable.Id].WebSearches);
        Assert.Equal(2, costs[outage.Id].Reports); // dzienny i drugi raport
        Assert.InRange(costs[vulnerable.Id].InputTokens, 3000, 3000 + 5000); // całe sprawdzanie + część analizy
        var day = AiPricing.Cost(new AiUsage(8000, 1200, 0, 0, 2, 2), Sonnet)!.Value;
        var second = AiPricing.Cost(analysis, Sonnet)!.Value;
        Assert.Equal(day + second, costs[vulnerable.Id].CostUsd!.Value + costs[outage.Id].CostUsd!.Value, 10);
        Assert.True(costs[vulnerable.Id].CostUsd > 2 * AiPricing.PerWebSearch);
    }

    [Fact]
    public async Task Older_report_without_researched_ids_uses_the_vulnerability_pattern_and_unknown_model_has_tokens_only()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var vulnerable = await app.AddItemAsync("csirt-cez", "Ostrzeżenie", "https://csirt.cez.gov.pl/a", "Luka CVE-2026-12345 w VPN.");
        var outage = await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w P1", "https://ezdrowie.gov.pl/b", "Przerwa techniczna.");
        await AddReportAsync(app, new DateOnly(2026, 11, 18), Sonnet, new AiUsage(8000, 1200, 0, 0, 2, 3), [vulnerable.Id, outage.Id],
            new VulnerabilityResearch("notatki", [], 3));
        var unknown = await app.AddItemAsync("nfz-aktualnosci", "Inny model", "https://www.nfz.gov.pl/x,9.html", "Treść.");
        await AddReportAsync(app, new DateOnly(2026, 11, 19), "inny-model", new AiUsage(1000, 100, 0, 0, 1), [unknown.Id]);

        await using var db = await app.DbAsync();
        var costs = await EntryCosts.EstimateAsync(db, [vulnerable.Id, outage.Id, unknown.Id]);

        Assert.Equal(3, costs[vulnerable.Id].WebSearches);
        Assert.Equal(0, costs[outage.Id].WebSearches);
        Assert.Null(costs[unknown.Id].CostUsd);
        Assert.Equal(1000, costs[unknown.Id].InputTokens);
    }
}
