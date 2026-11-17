using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;

namespace ZdrowieInfo.Server.Sources.Scheduling;

/// <summary>Pętla co minutę: stan bazowy, sprawdzenie o 5:00 (z nadrabianiem), ponowienia, porządki raz na dobę.</summary>
public sealed class SchedulerService(SourceScheduler scheduler, TimeProvider time, IOptions<SourcesOptions> options, ILogger<SchedulerService> logger)
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
