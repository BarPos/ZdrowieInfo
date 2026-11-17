using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests;

public class AccountFlowTests(PostgresFixture postgres)
{
    private const string Login = "bartek";
    private const string Password = "Test-Password-1234";

    [Fact]
    public async Task Anonymous_user_is_redirected_to_login()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var browser = app.CreateBrowser();

        foreach (var path in new[] { "/", "/panel", "/Account/Manage" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/Account/Login", response.RedirectPath());
        }
    }

    [Fact]
    public async Task Registration_and_email_pages_do_not_exist()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var browser = app.CreateBrowser();

        foreach (var path in new[] { "/Account/Register", "/Account/ForgotPassword", "/Account/ExternalLogin" })
            Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Public_assets_and_security_headers()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var browser = app.CreateBrowser();

        var login = await browser.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("script-src 'self'", login.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", login.Headers.GetValues("X-Frame-Options").Single());

        var html = await login.Content.ReadAsStringAsync();
        var blazorJs = System.Text.RegularExpressions.Regex.Match(html, "_framework/blazor\\.web[^\"]*\\.js").Value;
        Assert.NotEmpty(blazorJs);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/" + blazorJs)).StatusCode);

        var sw = await browser.GetAsync("/service-worker.js");
        Assert.Equal(HttpStatusCode.OK, sw.StatusCode);
        Assert.Contains($"const VERSION = '{Common.AppVersion.Current}'", await sw.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/manifest.webmanifest")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/icons/icon-192.png")).StatusCode);
    }

    [Fact]
    public async Task First_login_forces_authenticator_setup_then_two_factor_login_works()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await app.CreateUserAsync(Login, Password);
        var browser = app.CreateBrowser();

        // 1. Hasło bez skonfigurowanego TOTP: zalogowany, ale wszystko kieruje do konfiguracji.
        var login = await PasswordLoginAsync(browser);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var home = await browser.GetAsync("/");
        Assert.Equal("/Account/Manage/EnableAuthenticator", home.RedirectPath());
        Assert.Equal("/Account/Manage/EnableAuthenticator", (await browser.GetAsync("/panel")).RedirectPath());

        // 2. Konfiguracja TOTP: klucz ze strony, kod jak z telefonu, kody odzyskiwania.
        var key = await EnableAuthenticatorAsync(browser);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);

        // 3. Wylogowanie i ponowne logowanie: hasło → kod.
        await LogoutAsync(browser);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/")).StatusCode);

        var second = await PasswordLoginAsync(browser);
        Assert.StartsWith("/Account/LoginWith2fa", second.RedirectPath());
        var twoFactor = await browser.SubmitAsync(second.RedirectPath()!, "login-with-2fa",
            new Dictionary<string, string> { ["Input.TwoFactorCode"] = Totp.Code(key) });
        Assert.Equal(HttpStatusCode.Redirect, twoFactor.StatusCode);

        // 4. Sesja trwała 30 dni.
        var setCookie = twoFactor.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("zdrowieinfo.auth="));
        var expires = DateTimeOffset.Parse(setCookie.Split(';').Single(p => p.Trim().StartsWith("expires=", StringComparison.OrdinalIgnoreCase)).Split('=')[1]);
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromDays(29.9), TimeSpan.FromDays(30.1));
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Wrong_two_factor_code_is_rejected()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await app.CreateUserAsync(Login, Password);
        var browser = app.CreateBrowser();
        await PasswordLoginAsync(browser);
        var key = await EnableAuthenticatorAsync(browser);
        await LogoutAsync(browser);

        var login = await PasswordLoginAsync(browser);
        var wrong = Totp.Code(key) == "000000" ? "111111" : "000000";
        var response = await browser.SubmitAsync(login.RedirectPath()!, "login-with-2fa",
            new Dictionary<string, string> { ["Input.TwoFactorCode"] = wrong });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
        Assert.Contains("nieprawidłowy kod", page.QuerySelector("[role=alert]")!.TextContent);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await app.CreateUserAsync(Login, Password);
        var browser = app.CreateBrowser();

        for (var i = 0; i < 5; i++)
            await PasswordLoginAsync(browser, "zle-haslo-" + i);

        var response = await PasswordLoginAsync(browser);
        Assert.Equal("/Account/Lockout", response.RedirectPath());
    }

    [Fact]
    public async Task Session_survives_server_restart()
    {
        var dataDir = Directory.CreateTempSubdirectory("zdrowieinfo-test-").FullName;
        string connectionString;
        string cookie;

        await using (var first = await AppFactory.CreateAsync(postgres, dataDir))
        {
            connectionString = first.ConnectionString;
            await first.CreateUserAsync(Login, Password);
            var browser = first.CreateBrowser();
            await PasswordLoginAsync(browser);
            await EnableAuthenticatorAsync(browser);
            var probe = new HttpRequestMessage(HttpMethod.Get, "/");
            var response = await browser.SendAsync(probe);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            cookie = ExtractAuthCookie(first, browser);
        }

        // „Restart”: nowy proces aplikacji, ta sama baza i ten sam katalog kluczy Data Protection.
        await using var second = await AppFactory.CreateAsync(postgres, dataDir, connectionString);
        var client = second.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    private static string ExtractAuthCookie(AppFactory app, HttpClient browser)
    {
        // WebApplicationFactory trzyma ciasteczka w CookieContainer handlera; odczytujemy je przez zwykłe żądanie z nagłówkiem.
        var handlerField = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var handler = handlerField.GetValue(browser)!;
        while (handler is DelegatingHandler delegating && handler.GetType().Name != "CookieContainerHandler")
            handler = delegating.InnerHandler!;
        var container = (CookieContainer)handler.GetType().GetProperty("Container")!.GetValue(handler)!;
        var auth = container.GetCookies(new Uri("https://localhost")).Single(c => c.Name == "zdrowieinfo.auth");
        return $"{auth.Name}={auth.Value}";
    }

    private static Task<HttpResponseMessage> PasswordLoginAsync(HttpClient browser, string password = Password) =>
        browser.SubmitAsync("/Account/Login", "login", new Dictionary<string, string>
        {
            ["Input.UserName"] = Login,
            ["Input.Password"] = password,
            ["Input.RememberMe"] = "true",
        });

    private static async Task<string> EnableAuthenticatorAsync(HttpClient browser)
    {
        const string page = "/Account/Manage/EnableAuthenticator";
        var document = await browser.GetDocumentAsync(page);
        var key = document.QuerySelector("kbd")!.TextContent;
        Assert.NotNull(document.QuerySelector(".qr svg"));

        var response = await browser.SubmitAsync(document, page, "send-code",
            new Dictionary<string, string> { ["Input.Code"] = Totp.Code(key) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
        Assert.Equal(10, result.QuerySelectorAll(".recovery-codes li").Length);
        return key;
    }

    private static async Task LogoutAsync(HttpClient browser)
    {
        var document = await browser.GetDocumentAsync("/Account/Manage");
        var form = document.Forms.Single(f => f.Action.EndsWith("Account/Logout"));
        var token = form.QuerySelector<IHtmlInputElement>("input[name='__RequestVerificationToken']")!.Value;
        var response = await browser.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
