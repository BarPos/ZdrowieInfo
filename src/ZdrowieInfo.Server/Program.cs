using System.Globalization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Accounts;
using ZdrowieInfo.Server.ApiKeys;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Export;
using ZdrowieInfo.Server.Hosting;
using ZdrowieInfo.Server.Mcp;
using ZdrowieInfo.Server.Pwa;
using ZdrowieInfo.Server.Push;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Sources;
using ZdrowieInfo.Server.Updates;
using ZdrowieInfo.Web;

var culture = CultureInfo.GetCultureInfo("pl-PL");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

var command = CliCommand.Parse(args);
if (command.Kind == CliCommandKind.Invalid)
{
    Console.Error.WriteLine(command.Error);
    Console.Error.WriteLine(CliCommand.Usage);
    return 2;
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();

var appOptions = builder.Configuration.GetSection(ZdrowieInfoOptions.Section).Get<ZdrowieInfoOptions>() ?? new ZdrowieInfoOptions();
appOptions.ResolvePaths(builder.Environment.ContentRootPath);
builder.Services.AddOptions<ZdrowieInfoOptions>()
    .Bind(builder.Configuration.GetSection(ZdrowieInfoOptions.Section))
    .PostConfigure(o => o.ResolvePaths(builder.Environment.ContentRootPath))
    .ValidateDataAnnotations()
    .ValidateOnStart();
OptionsValidation.ThrowIfInvalid(appOptions);

builder.Services.AddZdrowieInfoCommon(appOptions);
builder.Services.AddZdrowieInfoData(builder.Configuration);
builder.Services.AddAccounts(appOptions, secureCookies: !builder.Environment.IsDevelopment());
builder.Services.AddAuthorization();
builder.Services.AddSources(builder.Configuration);
builder.Services.AddReports(builder.Configuration);
builder.Services.AddApiKeys();
builder.Services.AddReportExport(builder.Configuration);
builder.Services.AddPush();
builder.Services.AddZdrowieInfoMcp();
builder.Services.AddUpdates(builder.Configuration);
builder.Services.AddZdrowieInfoWeb();
builder.Services.AddHealthChecks();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in appOptions.TrustedProxies)
        o.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
});
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.DefaultRequestCulture = new RequestCulture(culture);
    o.SupportedCultures = o.SupportedUICultures = [culture];
    o.RequestCultureProviders.Clear();
});

var app = builder.Build();

if (command.Kind != CliCommandKind.Run)
    return await CliRunner.RunAsync(command, app.Services);

await DatabaseMigrator.MigrateAsync(app.Services);
await app.Services.GetRequiredService<SourceSeeder>().SeedAsync();
await app.Services.GetRequiredService<VapidKeyManager>().EnsureAsync();

app.UseForwardedHeaders();
app.UseSecurityHeaders();
app.UseRequestLocalization();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
// Strona błędu tylko dla przeglądarki. API, MCP i push (fetch) dostają gołe 401/403/404 – ponowne wykonanie POST
// na stronie Blazor skończyłoby się 400 z walidacji antiforgery.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/mcp")
               && !context.Request.Path.StartsWithSegments("/push"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

app.UseAuthentication();
app.UseAuthorization();
app.UseMfaEnforcement();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapServiceWorker();
// update.sh i install.sh uznają nową wersję za działającą, gdy /healthz odpowiada i podaje tę wersję.
app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = (context, report) =>
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { status = report.Status.ToString(), version = AppVersion.Current }));
    },
}).AllowAnonymous();
app.MapZdrowieInfoWeb();
app.MapReportsApi();
app.MapReportFiles();
app.MapReportActions();
app.MapPushEndpoints();
app.MapZdrowieInfoMcp();

app.Logger.LogInformation("ZdrowieInfo {Version} pod adresem {PublicUrl}", AppVersion.Current, app.Services.GetRequiredService<IOptions<ZdrowieInfoOptions>>().Value.PublicUrl);
await app.RunAsync();
return 0;

public partial class Program;
