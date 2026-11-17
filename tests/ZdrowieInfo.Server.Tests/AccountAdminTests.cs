using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZdrowieInfo.Server.Accounts;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Settings;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests;

public class AccountAdminTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Reset_mfa_disables_two_factor_and_unlocks()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await app.CreateUserAsync("bartek", "Test-Password-1234");

        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await users.FindByNameAsync("bartek"))!;
        await users.ResetAuthenticatorKeyAsync(user);
        await users.SetTwoFactorEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1));
        var stampBefore = await users.GetSecurityStampAsync(user);

        var result = await scope.ServiceProvider.GetRequiredService<UserAdminService>().ResetMfaAsync("bartek");
        Assert.True(result.Succeeded);

        user = (await users.FindByNameAsync("bartek"))!;
        Assert.False(await users.GetTwoFactorEnabledAsync(user));
        Assert.False(await users.IsLockedOutAsync(user));
        Assert.NotEqual(stampBefore, await users.GetSecurityStampAsync(user));
    }

    [Fact]
    public async Task Reset_mfa_for_unknown_user_fails()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await using var scope = app.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<UserAdminService>().ResetMfaAsync("nikt");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Short_password_is_rejected_in_polish()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        await using var scope = app.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<UserAdminService>().CreateUserAsync("bartek", "krotkie");
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Description.Contains("co najmniej 12 znaków"));
    }

    [Fact]
    public async Task Protected_settings_are_encrypted_at_rest()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var store = app.Services.GetRequiredService<AppSettingsStore>();

        await store.SetAsync("claude.api_key", "sk-test-sekret", isProtected: true);
        await store.SetAsync("clinic.profile", new { Name = "Przychodnia" });

        Assert.Equal("sk-test-sekret", await store.GetAsync<string>("claude.api_key"));

        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync();
        var raw = await db.AppSettings.AsNoTracking().SingleAsync(s => s.Key == "claude.api_key");
        Assert.True(raw.IsProtected);
        Assert.DoesNotContain("sk-test-sekret", raw.Value.RootElement.GetRawText());
    }
}
