using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ZdrowieInfo.Server.ApiKeys;
using ZdrowieInfo.Server.Tests.Infrastructure;
using ZdrowieInfo.Server.Tests.Search;

namespace ZdrowieInfo.Server.Tests.Mcp;

public class McpTests(PostgresFixture postgres)
{
    private static async Task<HttpClient> HttpAsync(AppFactory app, params string[] scopes)
    {
        var http = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (scopes.Length > 0)
        {
            var key = await app.Services.GetRequiredService<ApiKeyService>().CreateAsync("mcp", scopes);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.PlainText);
        }
        return http;
    }

    private static async Task<McpClient> ClientAsync(AppFactory app)
    {
        var http = await HttpAsync(app, ApiScopes.McpRead);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("https://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, loggerFactory: null, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?>? args = null)
    {
        var result = await client.CallToolAsync(tool, args ?? [], cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static StringContent InitializeRequest() => new(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
        Encoding.UTF8, "application/json");

    [Fact]
    public async Task Requires_key_with_mcp_scope()
    {
        await using var app = await AppFactory.CreateAsync(postgres);

        async Task<HttpStatusCode> Post(Task<HttpClient> client)
        {
            var http = await client;
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = InitializeRequest() };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            return (await http.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await Post(HttpAsync(app)));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(HttpAsync(app, ApiScopes.ReportsWrite)));
        Assert.Equal(HttpStatusCode.OK, await Post(HttpAsync(app, ApiScopes.McpRead)));
    }

    [Fact]
    public async Task Lists_read_only_tools()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await using var client = await ClientAsync(app);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["get_report", "list_sources", "search_reports"], tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.Equal("zdrowieinfo", client.ServerInfo.Name);
    }

    [Fact]
    public async Task Search_reports_returns_findings_with_source_and_report_urls()
    {
        await using var app = await SearchTests.StartWithReportsAsync(postgres);
        await using var client = await ClientAsync(app);

        var found = await CallAsync(client, "search_reports", new() { ["query"] = "fortigate" });
        var first = found.GetProperty("results")[0];
        Assert.Equal("Krytyczna podatność FortiGate", first.GetProperty("headline").GetString());
        Assert.Equal("wysoka", first.GetProperty("importance").GetString());
        Assert.Equal("bezpieczeństwo", first.GetProperty("category").GetString());
        Assert.Equal("https://localhost/raporty/2026-11-16", first.GetProperty("report_url").GetString());
        Assert.StartsWith("https://example.pl/", first.GetProperty("sources")[0].GetProperty("url").GetString());

        var filtered = await CallAsync(client, "search_reports", new() { ["query"] = "przerw", ["importance"] = "krytyczna" });
        Assert.Equal(1, filtered.GetProperty("count").GetInt32());

        var bySource = await CallAsync(client, "search_reports", new() { ["source"] = "csirt-cez" });
        Assert.Equal("Krytyczna podatność FortiGate", bySource.GetProperty("results")[0].GetProperty("headline").GetString());
        Assert.Equal(1, bySource.GetProperty("count").GetInt32());

        var bad = await client.CallToolAsync("search_reports", new Dictionary<string, object?> { ["importance"] = "pilne" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(bad.IsError);
        Assert.Contains("krytyczna", Assert.IsType<TextContentBlock>(Assert.Single(bad.Content)).Text);
    }

    [Fact]
    public async Task Get_report_returns_numbered_findings_and_latest_by_default()
    {
        await using var app = await SearchTests.StartWithReportsAsync(postgres);
        await using var client = await ClientAsync(app);

        var report = await CallAsync(client, "get_report", new() { ["date"] = "2026-11-16" });
        Assert.Equal("Raport 16.11", report.GetProperty("title").GetString());
        Assert.Equal("https://localhost/eksport/raport-2026-11-16.pdf", report.GetProperty("pdf_url").GetString());
        var findings = report.GetProperty("findings");
        Assert.Equal("2.1", findings[0].GetProperty("number").GetString());
        Assert.Equal("Zaktualizować do 7.4.3.", findings[0].GetProperty("action").GetString());
        Assert.Equal("cytat", findings[0].GetProperty("citations")[0].GetProperty("quote").GetString());

        var latest = await CallAsync(client, "get_report");
        Assert.Equal("2026-11-17", latest.GetProperty("date").GetString());

        var missing = await client.CallToolAsync("get_report", new Dictionary<string, object?> { ["date"] = "2026-01-01" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(missing.IsError);
    }

    [Fact]
    public async Task List_sources_shows_status_and_open_incident()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await using var client = await ClientAsync(app);

        var result = await CallAsync(client, "list_sources");
        Assert.Equal(23, result.GetProperty("count").GetInt32());
        var csirt = result.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "csirt-cez");
        Assert.StartsWith("https://", csirt.GetProperty("url").GetString());
        Assert.Equal("jeszcze nie sprawdzone", csirt.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, csirt.GetProperty("open_incident").ValueKind);
    }
}
