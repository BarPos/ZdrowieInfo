using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Sources;

namespace ZdrowieInfo.Server.Tests.Infrastructure;

/// <summary>Dane testowe w bazie i zalogowana „przeglądarka”.</summary>
public static class TestData
{
    public const string Login = "bartek";
    public const string Password = "Test-Password-1234";

    public static async Task<ZdrowieInfoDbContext> DbAsync(this AppFactory app) =>
        await app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync();

    /// <summary>Nowy (niezaraportowany) wpis źródła.</summary>
    public static async Task<SourceItem> AddItemAsync(this AppFactory app, string slug, string title, string url, string? content,
        DateOnly? published = null, bool baseline = false, SourceItemKind kind = SourceItemKind.Entry)
    {
        await using var db = await app.DbAsync();
        var source = await db.Sources.SingleAsync(s => s.Slug == slug);
        var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var item = new SourceItem
        {
            SourceId = source.Id, Kind = kind, Key = ZdrowieInfo.Server.Sources.Extraction.UrlNormalizer.Key(url), Url = url, Title = title,
            PublishedOn = published, Content = content, IsBaseline = baseline, FirstSeenAt = now, LastChangedAt = now,
            DetailFetchedAt = content is null ? null : now,
        };
        db.SourceItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    /// <summary>Przeglądarka po pełnym logowaniu: hasło + skonfigurowane TOTP (domyślnie administrator).</summary>
    public static async Task<HttpClient> SignedInBrowserAsync(this AppFactory app, UserRole role = UserRole.Admin, string login = Login)
    {
        await app.CreateUserAsync(login, Password, role);
        var browser = app.CreateBrowser();
        await browser.SubmitAsync("/Account/Login", "login", new Dictionary<string, string>
        {
            ["Input.UserName"] = login, ["Input.Password"] = Password, ["Input.RememberMe"] = "true",
        });
        const string page = "/Account/Manage/EnableAuthenticator";
        var document = await browser.GetDocumentAsync(page);
        var key = document.QuerySelector("kbd")!.TextContent;
        var response = await browser.SubmitAsync(document, page, "send-code", new Dictionary<string, string> { ["Input.Code"] = Totp.Code(key) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return browser;
    }

    public static async Task<string> TextAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync()).Body!.TextContent;
    }
}

/// <summary>Generator z kolejką odpowiedzi ustawianych w teście (dokument albo wyjątek).</summary>
public sealed class ScriptedGenerator : ZdrowieInfo.Server.Reports.IReportGenerator
{
    public Queue<Func<ZdrowieInfo.Server.Reports.GenerationRequest, ZdrowieInfo.Server.Reports.GenerationResult>> Replies { get; } = new();
    public List<ZdrowieInfo.Server.Reports.GenerationRequest> Requests { get; } = [];

    public string Name => "claude";

    public Task<ZdrowieInfo.Server.Reports.GenerationResult> GenerateAsync(ZdrowieInfo.Server.Reports.GenerationRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        if (Replies.Count == 0) throw new InvalidOperationException("Generator wywołany ponad plan testu.");
        return Task.FromResult(Replies.Dequeue()(request));
    }

    public static ZdrowieInfo.Server.Reports.GenerationResult Result(ZdrowieInfo.Server.Reports.ReportDocument document) =>
        new(document, "claude-sonnet-5-5", new ZdrowieInfo.Server.Reports.AiUsage(1000, 200, 0, 0, 1));
}
