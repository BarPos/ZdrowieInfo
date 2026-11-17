using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Push;

/// <summary>
/// Rejestracja urządzeń do powiadomień (zalogowany użytkownik, nagłówek antiforgery – wywołania z pwa.js na stronie
/// Konto → Powiadomienia). Poza /api, bo /api to programy z kluczem API.
/// </summary>
public static class PushEndpoints
{
    public sealed record SubscribeRequest(string Endpoint, SubscribeKeys Keys);
    public sealed record SubscribeKeys(string P256dh, string Auth);
    public sealed record UnsubscribeRequest(string Endpoint);

    public static RouteGroupBuilder MapPushEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var push = endpoints.MapGroup("/push").RequireAuthorization();

        push.MapGet("/vapid-public-key", async (VapidKeyManager vapid, CancellationToken ct) =>
            Results.Text((await vapid.GetAsync(ct)).PublicKey));

        push.MapPost("/subscriptions", async (HttpContext http, SubscribeRequest request, IAntiforgery antiforgery,
            IDbContextFactory<ZdrowieInfoDbContext> dbFactory, VapidKeyManager vapid, TimeProvider time, CancellationToken ct) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest(new { error = "Brak albo nieprawidłowy token antiforgery." });
            if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrWhiteSpace(request.Keys?.P256dh) || string.IsNullOrWhiteSpace(request.Keys.Auth))
                return Results.BadRequest(new { error = "Nieprawidłowa subskrypcja." });

            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.PushSubscriptions.SingleOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);
            if (existing is null)
            {
                db.PushSubscriptions.Add(existing = new PushSubscriptionRecord
                {
                    UserId = userId, Endpoint = request.Endpoint, P256dh = request.Keys.P256dh, Auth = request.Keys.Auth,
                    Origin = (await vapid.GetAsync(ct)).PublicUrl, CreatedAt = time.GetUtcNow(),
                });
            }
            existing.UserId = userId;
            existing.P256dh = request.Keys.P256dh;
            existing.Auth = request.Keys.Auth;
            existing.Device = DeviceName(http.Request.Headers.UserAgent.ToString());
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = existing.Id });
        });

        push.MapPost("/subscriptions/delete", async (HttpContext http, UnsubscribeRequest request, IAntiforgery antiforgery,
            IDbContextFactory<ZdrowieInfoDbContext> dbFactory, CancellationToken ct) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest(new { error = "Brak albo nieprawidłowy token antiforgery." });
            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var removed = await db.PushSubscriptions.Where(s => s.Endpoint == request.Endpoint && s.UserId == userId).ExecuteDeleteAsync(ct);
            return Results.Ok(new { removed });
        });

        push.MapPost("/test", async (HttpContext http, IAntiforgery antiforgery, PushNotifier notifier, CancellationToken ct) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest(new { error = "Brak albo nieprawidłowy token antiforgery." });
            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var sent = await notifier.SendAsync(new PushMessage("ZdrowieInfo: powiadomienie testowe",
                "Powiadomienia działają. Raport dzienny przyjdzie rano.", "/", "test"), userId, ct);
            return Results.Ok(new { sent });
        });
        return push;
    }

    /// <summary>„iPhone · Safari”, „Mac · Chrome”… – żeby rozpoznać urządzenie na liście.</summary>
    public static string DeviceName(string userAgent)
    {
        var device = userAgent switch
        {
            _ when userAgent.Contains("iPhone") => "iPhone",
            _ when userAgent.Contains("iPad") => "iPad",
            _ when userAgent.Contains("Android") => "Android",
            _ when userAgent.Contains("Mac OS X") => "Mac",
            _ when userAgent.Contains("Windows") => "Windows",
            _ when userAgent.Contains("Linux") => "Linux",
            _ => "urządzenie",
        };
        var browser = userAgent switch
        {
            _ when userAgent.Contains("Edg/") => "Edge",
            _ when userAgent.Contains("Firefox/") => "Firefox",
            _ when userAgent.Contains("Chrome/") || userAgent.Contains("CriOS/") => "Chrome",
            _ when userAgent.Contains("Safari/") => "Safari",
            _ => "przeglądarka",
        };
        return $"{device} · {browser}";
    }
}
