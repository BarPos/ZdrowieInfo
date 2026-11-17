using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZdrowieInfo.Server.Updates;

/// <summary>
/// Przy starcie i co 5 minut: wynik aktualizacji (result.conf od update.sh – po udanej aktualizacji powstaje dopiero,
/// gdy nowa wersja już działa) i czy minęło <c>CheckIntervalHours</c> od ostatniego sprawdzenia wydań (tylko gdy
/// aktualizacje są skonfigurowane).
/// Polecenia wiersza poleceń (np. --migrate-only) nie uruchamiają usług w tle.
/// </summary>
public sealed class UpdatesBackgroundService(
    ServerUpdateService updates,
    IReleaseSource releases,
    IOptions<UpdatesOptions> options,
    TimeProvider time,
    ILogger<UpdatesBackgroundService> logger) : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await GuardAsync(() => updates.ProcessResultFileAsync(stoppingToken), "wynik aktualizacji");
        await GuardAsync(() => CheckIfDueAsync(stoppingToken), "sprawdzanie wydań");
        using var timer = new PeriodicTimer(TickInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await GuardAsync(() => updates.ProcessResultFileAsync(stoppingToken), "wynik aktualizacji");
            await GuardAsync(() => CheckIfDueAsync(stoppingToken), "sprawdzanie wydań");
        }
    }

    private async Task CheckIfDueAsync(CancellationToken ct)
    {
        if (releases.IsConfigured && await updates.IsCheckDueAsync(options.Value.CheckIntervalHours, ct))
            await updates.CheckNowAsync(ct);
    }

    private async Task GuardAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Aktualizacje: {What} nieudane.", what);
        }
    }
}
