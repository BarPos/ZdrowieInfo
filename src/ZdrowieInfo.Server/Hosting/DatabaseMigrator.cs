using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Hosting;

internal static class DatabaseMigrator
{
    /// <summary>Schemat wyłącznie z migracji EF Core. Wywoływane przy starcie i przez --migrate-only.</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0) return;

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ZdrowieInfo.Migrations");
        logger.LogInformation("Stosuję migracje: {Migrations}", string.Join(", ", pending));
        await db.Database.MigrateAsync(ct);
    }
}
