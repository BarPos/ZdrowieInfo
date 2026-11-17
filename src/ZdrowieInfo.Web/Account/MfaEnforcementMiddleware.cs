using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using ZdrowieInfo.Server.Accounts;

namespace ZdrowieInfo.Web.Account;

/// <summary>
/// Zalogowany użytkownik bez włączonego TOTP może otworzyć tylko konfigurację aplikacji uwierzytelniającej
/// (i zasoby potrzebne tej stronie). Wszystko inne przekierowuje tam.
/// </summary>
public sealed class MfaEnforcementMiddleware(RequestDelegate next)
{
    public const string SetupPath = "/Account/Manage/EnableAuthenticator";

    private static readonly string[] AllowedPrefixes =
    [
        SetupPath, "/Account/Logout", "/Account/Login", "/Account/Lockout", "/Account/AccessDenied",
        "/_framework/", "/_content/", "/_blazor", "/css/", "/js/", "/icons/",
        "/manifest.webmanifest", "/service-worker.js", "/favicon", "/healthz",
    ];

    public Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        // Tylko sesje z ciasteczka logowania. Programy z kluczem API nie mają TOTP – ich dostęp ogranicza zakres klucza.
        if (user.Identity?.IsAuthenticated == true
            && user.Identity.AuthenticationType == IdentityConstants.ApplicationScheme
            && !user.HasClaim(MfaClaims.Enabled, "1")
            && !IsAllowed(context.Request.Path))
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            context.Response.Redirect(SetupPath);
            return Task.CompletedTask;
        }
        return next(context);
    }

    private static bool IsAllowed(PathString path) =>
        AllowedPrefixes.Any(p => path.Value?.StartsWith(p, StringComparison.OrdinalIgnoreCase) == true);
}
