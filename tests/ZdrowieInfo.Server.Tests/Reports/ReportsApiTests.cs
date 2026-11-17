using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZdrowieInfo.Server.ApiKeys;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Reports;

public class ReportsApiTests(PostgresFixture postgres)
{
    private const string Quote = "Platforma Usług Inteligentnych (PUI) będzie niedostępna";
    private const string Content = "18 listopada 2026 roku w godzinach 20:00–24:00 Platforma Usług Inteligentnych (PUI) będzie niedostępna z powodu prac technicznych.";

    private static async Task<HttpClient> ClientAsync(AppFactory app, params string[] scopes)
    {
        var key = await app.Services.GetRequiredService<ApiKeyService>().CreateAsync("test", scopes);
        var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.PlainText);
        return client;
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body, ReportJson.Options), System.Text.Encoding.UTF8, "application/json");

    private static object Doc(string itemId, string quote = Quote) => new ReportDocument
    {
        Title = "Raport z agenta",
        Summary = "Przerwa w PUI.",
        Findings = [new FindingDto { Importance = "krytyczna", Category = "awaria", Headline = "PUI 20–24", Body = "Przerwa.", Citations = [new CitationDto { ItemId = itemId, Quote = quote }] }],
    };

    [Fact]
    public async Task Requires_valid_key_with_reports_scope()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var anonymous = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/items/pending")).StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "mon_abcdefgh_" + new string('x', 43));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/items/pending")).StatusCode);

        // POST bez klucza: 401, nie strona błędu (ponowne wykonanie POST na stronie Blazor dawało 400).
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient(new() { BaseAddress = new Uri("https://localhost") })
            .PostAsync("/api/reports?date=2026-11-18", Json(new { }))).StatusCode);

        var mcpOnly = await ClientAsync(app, ApiScopes.McpRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await mcpOnly.GetAsync("/api/items/pending")).StatusCode);

        var writer = await ClientAsync(app, ApiScopes.ReportsWrite);
        Assert.Equal(HttpStatusCode.OK, (await writer.GetAsync("/api/items/pending")).StatusCode);
    }

    [Fact]
    public async Task Revoked_key_stops_working()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var client = await ClientAsync(app, ApiScopes.ReportsWrite);
        var keys = app.Services.GetRequiredService<ApiKeyService>();
        await keys.RevokeAsync((await keys.ListAsync()).Single().Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/items/pending")).StatusCode);
    }

    [Fact]
    public async Task External_producer_reads_pending_items_and_posts_report()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var item = await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", Content);
        var client = await ClientAsync(app, ApiScopes.ReportsWrite);

        var pending = await client.GetFromJsonAsync<JsonElement>("/api/items/pending");
        var first = pending.EnumerateArray().Single();
        Assert.Equal(item.Id.ToString(), first.GetProperty("item_id").GetString());
        Assert.Equal(Content, first.GetProperty("content").GetString());

        var created = await client.PostAsync("/api/reports?date=2026-11-18", Json(Doc(item.Id.ToString())));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ready", body.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/reports?date=2026-11-18", Json(Doc(item.Id.ToString())))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/reports?date=2026-11-18&replace=true", Json(Doc(item.Id.ToString())))).StatusCode);

        await using var db = await app.DbAsync();
        var report = await db.Reports.SingleAsync();
        Assert.Equal("api:test", report.Producer);
        Assert.Equal(report.Id, (await db.SourceItems.SingleAsync(i => i.Id == item.Id)).ReportId);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/items/pending")).EnumerateArray());
    }

    [Fact]
    public async Task Invalid_replacement_changes_nothing()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var item = await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", Content);
        var client = await ClientAsync(app, ApiScopes.ReportsWrite);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/reports?date=2026-11-18", Json(Doc(item.Id.ToString())))).StatusCode);

        var invalid = await client.PostAsync("/api/reports?date=2026-11-18&replace=true", Json(Doc(item.Id.ToString(), "zmyślony cytat o PUI")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        await using var db = await app.DbAsync();
        var report = await db.Reports.SingleAsync();
        var saved = await db.SourceItems.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(report.Id, saved.ReportId);
        Assert.NotNull(saved.ReportedAt);
    }

    [Fact]
    public async Task Invented_quote_is_rejected_and_unknown_url_is_unverified()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var item = await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", Content);
        var client = await ClientAsync(app, ApiScopes.ReportsWrite);

        var invalid = await client.PostAsync("/api/reports?date=2026-11-18", Json(Doc(item.Id.ToString(), "PUI wyłączona na cały tydzień")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Contains("nie jest dosłownym fragmentem", await invalid.Content.ReadAsStringAsync());

        var external = new ReportDocument
        {
            Title = "Raport", Summary = "S",
            Findings = [new FindingDto { Importance = "niska", Category = "inne", Headline = "H", Body = "B", Citations = [new CitationDto { Url = "https://inna-strona.pl/a", Quote = "Coś z innego serwisu" }] }],
        };
        var created = await client.PostAsync("/api/reports?date=2026-11-18", Json(external));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("NeedsReview", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Report_pages_render_for_signed_in_user()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", Content);
        await app.Services.GetRequiredService<DailyReportService>().GenerateAsync(new DateOnly(2026, 11, 18));
        var browser = await app.SignedInBrowserAsync();

        var list = await browser.TextAsync("/");
        Assert.Contains("Raport przykładowy – 18.11.2026", list);
        Assert.Contains("1 wysokie", list);

        var page = await browser.TextAsync("/raporty/2026-11-18");
        Assert.Contains("Wysokie", page);
        Assert.Contains("Przerwa w PUI", page);
        Assert.Contains("18 listopada 2026 roku w godzinach 20:00–24:00", page);
        Assert.Contains("Stan źródeł", page);

        Assert.Contains("Nie ma takiego raportu", await browser.TextAsync("/raporty/2026-11-17"));
    }
}
