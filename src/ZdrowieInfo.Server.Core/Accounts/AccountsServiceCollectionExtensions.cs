using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Accounts;

public static class AccountsServiceCollectionExtensions
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan SessionRevalidationInterval = TimeSpan.FromMinutes(5);

    /// <summary>ASP.NET Core Identity: hasło + TOTP (+ passkey), blokada 5 prób / 15 min, sesja 30 dni.</summary>
    public static IServiceCollection AddAccounts(this IServiceCollection services, ZdrowieInfoOptions app, bool secureCookies)
    {
        services.AddAuthentication(o =>
            {
                o.DefaultScheme = IdentityConstants.ApplicationScheme;
                o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        services.AddIdentityCore<AppUser>(o =>
            {
                o.SignIn.RequireConfirmedAccount = false;
                o.User.RequireUniqueEmail = false;
                o.Password.RequiredLength = 12;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Stores.SchemaVersion = IdentitySchemaVersions.Version3; // passkey
            })
            .AddEntityFrameworkStores<ZdrowieInfoDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>()
            .AddErrorDescriber<PolishIdentityErrorDescriber>();

        services.Configure<IdentityPasskeyOptions>(o => o.ServerDomain = app.PublicUri.Host);

        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "zdrowieinfo.auth";
            o.ExpireTimeSpan = SessionLifetime;
            o.SlidingExpiration = true;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.LoginPath = "/Account/Login";
            o.LogoutPath = "/Account/Logout";
            o.AccessDeniedPath = "/Account/AccessDenied";
        });
        // Zmiana hasła, --set-password i --reset-mfa unieważniają sesje najpóźniej po tym czasie
        // (jeden użytkownik – częste sprawdzanie nic nie kosztuje).
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = SessionRevalidationInterval);

        services.AddAuthorizationBuilder()
            .AddPolicy(AppRoles.AdminPolicy, p => p
                .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
                .RequireAuthenticatedUser()
                .RequireRole(AppRoles.Admin));

        services.AddScoped<UserAdminService>();
        return services;
    }
}
