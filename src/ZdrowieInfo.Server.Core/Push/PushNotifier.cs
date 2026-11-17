using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Push;

/// <summary>
/// Wysyła powiadomienie na zarejestrowane urządzenia (wszystkie, jednego użytkownika albo tylko administratorów);
/// subskrypcje, które zwróciły 404/410, usuwa.
/// </summary>
public sealed class PushNotifier(IDbContextFactory<ZdrowieInfoDbContext> dbFactory, IPushSender sender, VapidKeyManager vapid, TimeProvider time, ILogger<PushNotifier> logger)
{
    /// <returns>Na ile urządzeń wysłano.</returns>
    public async Task<int> SendAsync(PushMessage message, string? userId = null, CancellationToken ct = default, bool adminsOnly = false)
    {
        var keys = await vapid.GetAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.PushSubscriptions.Where(s => userId == null || s.UserId == userId);
        if (adminsOnly) query = query.Where(s => db.Users.Any(u => u.Id == s.UserId && u.Role == UserRole.Admin));
        var subscriptions = await query.ToListAsync(ct);
        var sent = 0;
        foreach (var subscription in subscriptions)
        {
            var result = await sender.SendAsync(subscription, message, keys, ct);
            switch (result.Status)
            {
                case PushSendStatus.Sent:
                    subscription.LastSuccessAt = time.GetUtcNow();
                    sent++;
                    break;
                case PushSendStatus.Gone:
                    db.PushSubscriptions.Remove(subscription);
                    logger.LogInformation("Usunięto nieaktualną subskrypcję push ({Device}, {Error}).", subscription.Device, result.Error);
                    break;
                default:
                    logger.LogWarning("Push na {Device} nieudany: {Error}", subscription.Device, result.Error);
                    break;
            }
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Push „{Title}”: {Sent} z {Count} urządzeń.", message.Title, sent, subscriptions.Count);
        return sent;
    }
}
