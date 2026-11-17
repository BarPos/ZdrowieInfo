using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AngleSharp.Html.Parser;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Push;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Settings;
using ZdrowieInfo.Server.Sources;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Push;

/// <summary>Zamiast serwerów push: zapisuje wysłane wiadomości; wybrane adresy odpowiadają 410 Gone.</summary>
public sealed class FakePushSender : IPushSender
{
    public ConcurrentQueue<(string Endpoint, PushMessage Message)> Sent { get; } = new();
    public HashSet<string> GoneEndpoints { get; } = [];

    public Task<PushSendResult> SendAsync(PushSubscriptionRecord subscription, PushMessage message, VapidKeys keys, CancellationToken ct = default)
    {
        Assert.False(string.IsNullOrEmpty(keys.PublicKey));
        if (GoneEndpoints.Contains(subscription.Endpoint)) return Task.FromResult(new PushSendResult(PushSendStatus.Gone, "HTTP 410"));
        Sent.Enqueue((subscription.Endpoint, message));
        return Task.FromResult(new PushSendResult(PushSendStatus.Sent));
    }
}

public class PushTests(PostgresFixture postgres)
{
    private static async Task<(AppFactory App, FakePushSender Push)> StartAsync(PostgresFixture postgres)
    {
        var push = new FakePushSender();
        var app = await AppFactory.CreateAsync(postgres, services: s => s.AddSingleton<IPushSender>(push));
        return (app, push);
    }

    private static async Task AddSubscriptionAsync(AppFactory app, string endpoint)
    {
        await using var db = await app.DbAsync();
        var user = await db.Users.FirstOrDefaultAsync();
        if (user is null)
        {
            await app.CreateUserAsync("bartek", "Test-Password-1234");
            user = await db.Users.FirstAsync();
        }
        db.PushSubscriptions.Add(new PushSubscriptionRecord { UserId = user.Id, Endpoint = endpoint, P256dh = "p", Auth = "a", Origin = "https://localhost", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Vapid_keys_are_stable_and_replaced_with_subscriptions_when_public_url_changes()
    {
        var (app, _) = await StartAsync(postgres);
        await using var _app = app;
        var manager = app.Services.GetRequiredService<VapidKeyManager>();
        var first = await manager.EnsureAsync();
        Assert.Equal(first, await manager.EnsureAsync());
        Assert.Equal("https://localhost", first.PublicUrl);
        await AddSubscriptionAsync(app, "https://push.example/1");

        var moved = new VapidKeyManager(app.Services.GetRequiredService<AppSettingsStore>(),
            app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>(),
            Options.Create(new ZdrowieInfoOptions { PublicUrl = "https://nowa-domena.pl", DataDirectory = app.DataDirectory }),
            Options.Create(new SourcesOptions()), NullLogger<VapidKeyManager>.Instance);
        var second = await moved.EnsureAsync();

        Assert.NotEqual(first.PublicKey, second.PublicKey);
        Assert.Equal("https://nowa-domena.pl", second.PublicUrl);
        await using var db = await app.DbAsync();
        Assert.Equal(0, await db.PushSubscriptions.CountAsync());
    }

    [Fact]
    public async Task Daily_report_is_pushed_once_and_gone_subscriptions_are_removed()
    {
        var (app, push) = await StartAsync(postgres);
        await using var _app = app;
        await AddSubscriptionAsync(app, "https://push.example/telefon");
        await AddSubscriptionAsync(app, "https://push.example/stary");
        push.GoneEndpoints.Add("https://push.example/stary");
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", "Platforma Usług Inteligentnych (PUI) będzie niedostępna w nocy.");

        await app.Services.GetRequiredService<DailyReportService>().GenerateAsync(new DateOnly(2026, 11, 18));
        await app.Services.GetRequiredService<NotificationDispatcher>().DispatchPendingAsync();

        var (endpoint, message) = Assert.Single(push.Sent);
        Assert.Equal("https://push.example/telefon", endpoint);
        Assert.Equal("/raporty/2026-11-18", message.Url);
        Assert.Contains("wysokie: 1", message.Body);
        await using var db = await app.DbAsync();
        Assert.True((await db.Reports.SingleAsync()).PushSent);
        Assert.Equal(["https://push.example/telefon"], await db.PushSubscriptions.Select(s => s.Endpoint).ToListAsync());
    }

    [Fact]
    public async Task Empty_report_push_says_no_changes()
    {
        var (app, push) = await StartAsync(postgres);
        await using var _app = app;
        await AddSubscriptionAsync(app, "https://push.example/telefon");
        await app.Services.GetRequiredService<DailyReportService>().GenerateAsync(new DateOnly(2026, 11, 18));
        Assert.Equal("Brak zmian w monitorowanych źródłach.", Assert.Single(push.Sent).Message.Body);
    }

    [Fact]
    public async Task Incident_opening_and_recovery_are_pushed_once_each_and_old_ones_are_skipped()
    {
        var (app, push) = await StartAsync(postgres);
        await using var _app = app;
        await AddSubscriptionAsync(app, "https://push.example/telefon");
        var dispatcher = app.Services.GetRequiredService<NotificationDispatcher>();
        var now = DateTimeOffset.UtcNow;
        long id;
        await using (var db = await app.DbAsync())
        {
            var source = await db.Sources.SingleAsync(s => s.Slug == "nfz-mazowsze");
            var old = await db.Sources.SingleAsync(s => s.Slug == "csirt-cez");
            var incident = new SourceIncident { SourceId = source.Id, StartedAt = now.AddMinutes(-15), LastAttemptAt = now, Attempts = 3, ErrorType = CheckErrorType.Blocked, Details = "Strona blokady (Imperva Incapsula)." };
            db.SourceIncidents.Add(incident);
            db.SourceIncidents.Add(new SourceIncident { SourceId = old.Id, StartedAt = now.AddDays(-3), LastAttemptAt = now.AddDays(-3), EndedAt = now.AddDays(-2), Attempts = 5, ErrorType = CheckErrorType.Http });
            await db.SaveChangesAsync();
            id = incident.Id;
        }

        await dispatcher.DispatchPendingAsync();
        await dispatcher.DispatchPendingAsync(); // bez duplikatów
        var failure = Assert.Single(push.Sent).Message;
        Assert.Equal("Brak dostępu do źródła: Mazowiecki OW NFZ – aktualności dla świadczeniodawców", failure.Title);
        Assert.Contains("blokada", failure.Body);
        Assert.Equal("/incydenty", failure.Url);

        await using (var db = await app.DbAsync())
            await db.SourceIncidents.Where(i => i.Id == id).ExecuteUpdateAsync(u => u.SetProperty(i => i.EndedAt, now.AddHours(2)));
        await dispatcher.DispatchPendingAsync();

        Assert.Equal(2, push.Sent.Count);
        Assert.StartsWith("Dostęp przywrócony: Mazowiecki OW NFZ", push.Sent.Last().Message.Title);
        Assert.Contains("2,3 h", push.Sent.Last().Message.Body); // 15 min przed + 2 h po
        await using var check = await app.DbAsync();
        Assert.All(await check.SourceIncidents.ToListAsync(), i => Assert.True(i.FailurePushSent));
    }

    [Fact]
    public async Task Device_registers_with_antiforgery_token_and_gets_test_notification()
    {
        var (app, push) = await StartAsync(postgres);
        await using var _app = app;
        var browser = await app.SignedInBrowserAsync();
        var page = new HtmlParser().ParseDocument(await browser.GetStringAsync("/Account/Manage/Notifications"));
        var token = page.QuerySelector("[data-push-panel] input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        var subscription = new { endpoint = "https://fcm.googleapis.com/fcm/send/abc", keys = new { p256dh = "BPk", auth = "x1" } };

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/push/subscriptions", subscription)).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/push/subscriptions") { Content = JsonContent.Create(subscription) };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1");
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(request)).StatusCode);

        var test = new HttpRequestMessage(HttpMethod.Post, "/push/test") { Content = JsonContent.Create(new { }) };
        test.Headers.Add("RequestVerificationToken", token);
        var result = await (await browser.SendAsync(test)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, result.GetProperty("sent").GetInt32());
        Assert.Equal("ZdrowieInfo: powiadomienie testowe", Assert.Single(push.Sent).Message.Title);

        Assert.Contains("iPhone · Safari", await browser.TextAsync("/Account/Manage/Notifications"));
        Assert.Equal(HttpStatusCode.Redirect, (await app.CreateBrowser().GetAsync("/push/vapid-public-key")).StatusCode);
    }

    [Fact]
    public async Task Incident_log_lists_incidents_with_filters()
    {
        var (app, _) = await StartAsync(postgres);
        await using var _app = app;
        await using (var db = await app.DbAsync())
        {
            var source = await db.Sources.SingleAsync(s => s.Slug == "nfz-mazowsze");
            db.SourceIncidents.Add(new SourceIncident { SourceId = source.Id, StartedAt = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero), EndedAt = new DateTimeOffset(2026, 11, 10, 11, 0, 0, TimeSpan.Zero), LastAttemptAt = new DateTimeOffset(2026, 11, 10, 11, 0, 0, TimeSpan.Zero), Attempts = 6, ErrorType = CheckErrorType.Timeout, Details = "Przekroczony czas oczekiwania." });
            await db.SaveChangesAsync();
        }
        var browser = await app.SignedInBrowserAsync();

        var all = await browser.TextAsync("/incydenty");
        Assert.Contains("Mazowiecki OW NFZ", all);
        Assert.Contains("przekroczony czas", all);
        Assert.Contains("3 h", all);
        Assert.Contains("Brak incydentów", await browser.TextAsync("/incydenty?od=2026-11-15"));
    }

    [Fact]
    public async Task Incident_pushes_go_to_admins_only_but_reports_to_everyone()
    {
        var (app, push) = await StartAsync(postgres);
        await using var _app = app;
        await app.CreateUserAsync("szef", "Test-Password-1234");
        await app.CreateUserAsync("czytelnik", "Test-Password-1234", UserRole.User);
        await using (var db = await app.DbAsync())
        {
            foreach (var user in await db.Users.ToListAsync())
                db.PushSubscriptions.Add(new PushSubscriptionRecord { UserId = user.Id, Endpoint = $"https://push.example/{user.UserName}", P256dh = "p", Auth = "a", Origin = "https://localhost", CreatedAt = DateTimeOffset.UtcNow });
            var source = await db.Sources.SingleAsync(s => s.Slug == "nfz-mazowsze");
            var now = DateTimeOffset.UtcNow;
            db.SourceIncidents.Add(new SourceIncident { SourceId = source.Id, StartedAt = now.AddMinutes(-15), LastAttemptAt = now, Attempts = 3, ErrorType = CheckErrorType.Http, Details = "HTTP 503" });
            await db.SaveChangesAsync();
        }
        await app.Services.GetRequiredService<NotificationDispatcher>().DispatchPendingAsync();
        Assert.Equal(["https://push.example/szef"], push.Sent.Select(s => s.Endpoint).ToArray());

        await app.Services.GetRequiredService<ZdrowieInfo.Server.Reports.DailyReportService>().GenerateAsync(DateOnly.FromDateTime(DateTime.Now));
        Assert.Equal(2, push.Sent.Count(s => s.Message.Url.StartsWith("/raporty/")));
    }
}
