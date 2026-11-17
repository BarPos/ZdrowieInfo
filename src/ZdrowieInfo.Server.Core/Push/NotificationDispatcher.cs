using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Sources;

namespace ZdrowieInfo.Server.Push;

/// <summary>
/// Wysyła zaległe powiadomienia: o raporcie dziennym, o otwarciu incydentu i o przywróceniu dostępu. Flagi w bazie
/// (PushSent, FailurePushSent, RecoveryPushSent) chronią przed duplikatami także po restarcie; stare zdarzenia (ponad dobę)
/// są oznaczane bez wysyłki, żeby po włączeniu powiadomień nie przyszła lawina.
/// </summary>
public sealed class NotificationDispatcher(IDbContextFactory<ZdrowieInfoDbContext> dbFactory, PushNotifier notifier, TimeProvider time, ILogger<NotificationDispatcher> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task DispatchPendingAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            var fresh = now.AddDays(-1);
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            foreach (var report in await db.Reports.Include(r => r.Findings).Where(r => !r.PushSent).OrderBy(r => r.Date).ToListAsync(ct))
            {
                if (report.CreatedAt >= fresh) await notifier.SendAsync(ReportMessage(report), ct: ct);
                report.PushSent = true;
                await db.SaveChangesAsync(ct);
            }

            var incidents = await (from i in db.SourceIncidents
                                   join s in db.Sources on i.SourceId equals s.Id
                                   where !i.FailurePushSent || (i.EndedAt != null && !i.RecoveryPushSent)
                                   select new { Incident = i, s.Name }).ToListAsync(ct);
            foreach (var x in incidents.OrderBy(x => x.Incident.StartedAt))
            {
                var incident = x.Incident;
                if (!incident.FailurePushSent)
                {
                    // Incydent, który zdążył się zamknąć, zanim poszło powiadomienie, dostaje jedno powiadomienie o przywróceniu.
                    if (incident.EndedAt is null && incident.LastAttemptAt >= fresh)
                        await notifier.SendAsync(new PushMessage($"Brak dostępu do źródła: {x.Name}",
                            $"{Describe(incident.ErrorType)} – {incident.Details}", "/incydenty", $"incydent-{incident.Id}"), ct: ct, adminsOnly: true);
                    incident.FailurePushSent = true;
                }
                if (incident.EndedAt is { } ended && !incident.RecoveryPushSent)
                {
                    if (ended >= fresh)
                        await notifier.SendAsync(new PushMessage($"Dostęp przywrócony: {x.Name}",
                            $"Przerwa trwała {Duration(ended - incident.StartedAt)} ({Describe(incident.ErrorType)}).", "/incydenty", $"incydent-{incident.Id}"), ct: ct, adminsOnly: true);
                    incident.RecoveryPushSent = true;
                }
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Błąd wysyłki powiadomień push.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public static PushMessage ReportMessage(Report report)
    {
        var url = $"/raporty/{report.Date:yyyy-MM-dd}";
        if (report.IsEmpty)
            return new PushMessage($"Raport {report.Date:dd.MM}: brak zmian", "Brak zmian w monitorowanych źródłach.", url, $"raport-{report.Date:yyyy-MM-dd}");

        var critical = report.Findings.Count(f => f.Importance == Importance.Critical);
        var high = report.Findings.Count(f => f.Importance == Importance.High);
        var counts = critical + high > 0
            ? $"Krytyczne: {critical}, wysokie: {high}. "
            : $"Punktów: {report.Findings.Count}. ";
        var prefix = report.Status switch
        {
            ReportStatus.AiError => "Bez analizy – ",
            ReportStatus.NeedsReview => "Do weryfikacji – ",
            _ => "",
        };
        return new PushMessage(report.Title, prefix + counts + report.Summary, url, $"raport-{report.Date:yyyy-MM-dd}");
    }

    public static string Describe(CheckErrorType type) => type switch
    {
        CheckErrorType.Http => "błąd HTTP",
        CheckErrorType.Timeout => "przekroczony czas",
        CheckErrorType.Network => "błąd sieci",
        CheckErrorType.Blocked => "blokada",
        CheckErrorType.NoItems => "brak wpisów (zmiana układu strony?)",
        CheckErrorType.RobotsDisallowed => "zakaz w robots.txt",
        CheckErrorType.TooLarge => "za duża strona",
        _ => type.ToString(),
    };

    public static string Duration(TimeSpan d) =>
        d.TotalMinutes < 60 ? $"{d.TotalMinutes:0} min" : d.TotalHours < 48 ? $"{d.TotalHours:0.#} h" : $"{d.TotalDays:0.#} dni";
}
