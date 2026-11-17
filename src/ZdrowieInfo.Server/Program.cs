using System.Globalization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Accounts;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Hosting;
using ZdrowieInfo.Server.Pwa;
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

app.UseForwardedHeaders();
app.UseSecurityHeaders();
app.UseRequestLocalization();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseMfaEnforcement();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapServiceWorker();
app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapZdrowieInfoWeb();

app.Logger.LogInformation("ZdrowieInfo {Version} pod adresem {PublicUrl}", AppVersion.Current, app.Services.GetRequiredService<IOptions<ZdrowieInfoOptions>>().Value.PublicUrl);
await app.RunAsync();
return 0;

public partial class Program;
