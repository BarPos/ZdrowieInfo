using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZdrowieInfo.Server.ApiKeys;

/// <summary>Schemat <c>ApiKey</c>: nagłówek <c>Authorization: Bearer mon_…</c>.</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "ApiKey";
    public const string ScopeClaim = "scope";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var key = await keys.ValidateAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (key is null) return AuthenticateResult.Fail("Nieprawidłowy albo unieważniony klucz API.");

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, key.Id.ToString()), new(ClaimTypes.Name, key.Name) };
        claims.AddRange(key.Scopes.Select(s => new Claim(ScopeClaim, s)));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)), Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}

public static class ApiKeysServiceCollectionExtensions
{
    public const string RateLimitPolicy = "api";

    /// <summary>Klucze API: schemat uwierzytelniania, polityka na każdy zakres, limit 60 zapytań na minutę na klucz.</summary>
    public static IServiceCollection AddApiKeys(this IServiceCollection services)
    {
        services.AddSingleton<ApiKeyService>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.Scheme, null);
        services.AddAuthorization(o =>
        {
            foreach (var scope in ApiScopes.All)
                o.AddPolicy(scope, p => p
                    .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.Scheme)
                    .RequireAuthenticatedUser()
                    .RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, scope));
        });
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Request.Headers.Authorization.ToString() is { Length: > 12 } auth ? auth[..Math.Min(auth.Length, 20)] : context.Connection.RemoteIpAddress?.ToString() ?? "anon",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }
}
