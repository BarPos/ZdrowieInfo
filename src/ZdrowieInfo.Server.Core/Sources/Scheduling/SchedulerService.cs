using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Push;
using ZdrowieInfo.Server.Reports;

namespace ZdrowieInfo.Server.Sources.Scheduling;

/// <summary>Pętla co minutę: stan bazowy, sprawdzenie o 5:00 (z nadrabianiem), ponowienia, raport dzienny od 5:30, porządki raz na dobę.</summary>
public sealed class SchedulerService(SourceScheduler scheduler, ReportScheduler reports, NotificationDispatcher notifications, TimeProvider time, IOptions<SourcesOptions> options, ILogger<SchedulerService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.SchedulerEnabled)
        {
            logger.LogWarning("Harmonogram sprawdzania źródeł jest wyłączony (ZdrowieInfo:Sources:SchedulerEnabled=false).");
            return;
        }

        DateOnly? lastCleanup = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scheduler.RunDueAsync(stoppingToken);
                await reports.RunDueAsync(stoppingToken);
                await notifications.DispatchPendingAsync(stoppingToken); // zaległe po restarcie albo nieudanej wysyłce

                var today = DateOnly.FromDateTime(WarsawClock.ToLocal(time.GetUtcNow()));
                if (lastCleanup != today)
                {
                    await scheduler.CleanupAsync(stoppingToken);
                    lastCleanup = today;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Błąd harmonogramu sprawdzania źródeł.");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
