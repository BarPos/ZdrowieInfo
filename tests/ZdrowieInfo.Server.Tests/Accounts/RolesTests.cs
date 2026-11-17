using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZdrowieInfo.Server.Accounts;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Accounts;

public class RolesTests(PostgresFixture postgres)
{
    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string url) => (await client.GetAsync(url)).StatusCode;

    private static async Task AssertDeniedAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task User_reads_and_searches_reports_but_admin_areas_are_denied_on_the_server()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await app.AddItemAsync("ezdrowie-podmioty", "Przerwa w PUI", "https://ezdrowie.gov.pl/portal/artykul/pui", "Platforma Usług Inteligentnych (PUI) będzie niedostępna w nocy.");
        await app.Services.GetRequiredService<DailyReportService>().GenerateAsync(new DateOnly(2026, 11, 18));
        var user = await app.SignedInBrowserAsync(UserRole.User, "czytelnik");

        foreach (var url in new[] { "/", "/szukaj?q=pui", "/raporty/2026-11-18", "/Account/Manage", "/Account/Manage/Notifications", "/Account/Manage/ChangePassword" })
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(user, url));
        foreach (var url in new[] { "/incydenty", "/panel", "/panel/zrodla", "/panel/raporty", "/panel/uzytkownicy", "/panel/klucze", "/panel/aktualizacje", "/eksport/raport-2026-11-18.pdf" })
            await AssertDeniedAsync(user, url);

        var report = await (await user.GetAsync("/raporty/2026-11-18")).Content.ReadAsStringAsync();
        Assert.Contains("Przerwa w PUI", report);
        Assert.DoesNotContain("Pobierz PDF", report);
        Assert.DoesNotContain("udostepnij", report);
        var menu = await (await user.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("href=\"panel\"", menu);
        Assert.DoesNotContain("href=\"incydenty\"", menu);

        // Akcje administratora (POST) też odrzucone, nawet z poprawnym tokenem formularza.
        var post = await user.PostAsync("/raporty/2026-11-18/usun", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", post.Headers.Location!.PathAndQuery);
        await using var db = await app.DbAsync();
        Assert.Equal(1, await db.Reports.CountAsync());
    }

    [Fact]
    public async Task Admin_sees_panel_incidents_and_report_tools()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await app.Services.GetRequiredService<DailyReportService>().GenerateAsync(new DateOnly(2026, 11, 18));
        var admin = await app.SignedInBrowserAsync();

        foreach (var url in new[] { "/incydenty", "/panel", "/panel/raporty", "/panel/uzytkownicy" })
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(admin, url));
        var report = await (await admin.GetAsync("/raporty/2026-11-18")).Content.ReadAsStringAsync();
        Assert.Contains("Pobierz PDF", report);
        Assert.Contains("Utwórz publiczny link do PDF", report);
        Assert.DoesNotContain(".tex", report);
    }

    [Fact]
    public async Task Last_admin_and_own_account_are_protected()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await app.CreateUserAsync("szef", TestData.Password);
        await app.CreateUserAsync("czytelnik", TestData.Password, UserRole.User);
        await using var scope = app.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdminService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var boss = (await users.FindByNameAsync("szef"))!;
        var reader = (await users.FindByNameAsync("czytelnik"))!;

        Assert.False((await admin.SetRoleAsync(boss.Id, UserRole.User, actingUserId: boss.Id)).Succeeded); // sam sobie
        Assert.False((await admin.SetRoleAsync(boss.Id, UserRole.User, actingUserId: reader.Id)).Succeeded); // jedyny administrator
        Assert.False((await admin.DeleteAsync(boss.Id, actingUserId: reader.Id)).Succeeded);
        Assert.False((await admin.DeleteAsync(boss.Id, actingUserId: boss.Id)).Succeeded);

        Assert.True((await admin.SetRoleAsync(reader.Id, UserRole.Admin, actingUserId: boss.Id)).Succeeded);
        var stamp = (await users.FindByIdAsync(boss.Id))!.SecurityStamp;
        Assert.True((await admin.SetRoleAsync(boss.Id, UserRole.User, actingUserId: reader.Id)).Succeeded); // już nie jedyny
        Assert.NotEqual(stamp, (await users.FindByIdAsync(boss.Id))!.SecurityStamp); // odebranie roli wylogowuje
        Assert.True((await admin.DeleteAsync(boss.Id, actingUserId: reader.Id)).Succeeded);

        var list = await admin.ListAsync();
        Assert.Equal("czytelnik", Assert.Single(list).Login);
        Assert.Equal(UserRole.Admin, list[0].Role);
    }

    [Fact]
    public async Task Removing_2fa_forces_setup_again_and_new_password_works()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await app.SignedInBrowserAsync(UserRole.User, "czytelnik"); // konto z włączonym TOTP
        string id;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByNameAsync("czytelnik"))!;
            Assert.True(user.TwoFactorEnabled);
            id = user.Id;
            var admin = scope.ServiceProvider.GetRequiredService<UserAdminService>();
            Assert.True((await admin.ResetMfaByIdAsync(id)).Succeeded);
            Assert.True((await admin.SetPasswordAsync(id, "Nowe-Haslo-123456")).Succeeded);
        }

        // Logowanie samym hasłem prowadzi do ponownej konfiguracji aplikacji uwierzytelniającej.
        var browser = app.CreateBrowser();
        await browser.SubmitAsync("/Account/Login", "login", new Dictionary<string, string>
        {
            ["Input.UserName"] = "czytelnik", ["Input.Password"] = "Nowe-Haslo-123456", ["Input.RememberMe"] = "true",
        });
        var home = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Contains("/Account/Manage/EnableAuthenticator", home.Headers.Location!.ToString());
    }

    [Fact]
    public void Generated_password_meets_policy()
    {
        for (var i = 0; i < 50; i++)
        {
            var password = UserAdminService.GeneratePassword();
            Assert.Equal(16, password.Length);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsDigit);
        }
    }

    [Fact]
    public void Cli_creates_admin_by_default_and_user_on_request()
    {
        Assert.Null(Hosting.CliCommand.Parse(["--create-user", "ala"]).Role);
        Assert.Equal("uzytkownik", Hosting.CliCommand.Parse(["--create-user", "ala", "--role", "uzytkownik"]).Role);
        Assert.Equal(Hosting.CliCommandKind.Invalid, Hosting.CliCommand.Parse(["--create-user", "ala", "--role", "szef"]).Kind);
    }
}
