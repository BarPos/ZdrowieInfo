using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Settings;

/// <summary>Odczyt i zapis <c>app_settings</c>. Wartości chronione (klucze API, VAPID) szyfrowane Data Protection.</summary>
public sealed class AppSettingsStore(IDbContextFactory<ZdrowieInfoDbContext> dbFactory, IDataProtectionProvider protection, TimeProvider time)
{
    private readonly IDataProtector _protector = protection.CreateProtector("ZdrowieInfo.AppSettings.v1");

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AppSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key, ct);
        if (row is null) return default;
        if (!row.IsProtected) return row.Value.Deserialize<T>();

        var cipher = row.Value.Deserialize<string>() ?? throw new InvalidOperationException($"Pusta wartość chroniona: {key}");
        return JsonSerializer.Deserialize<T>(_protector.Unprotect(cipher));
    }

    public async Task SetAsync<T>(string key, T value, bool isProtected = false, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value);
        var stored = isProtected ? JsonSerializer.Serialize(_protector.Protect(json)) : json;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == key, ct);
        if (row is null)
        {
            row = new AppSetting { Key = key, Value = JsonDocument.Parse(stored) };
            db.AppSettings.Add(row);
        }
        else
        {
            row.Value = JsonDocument.Parse(stored);
        }
        row.IsProtected = isProtected;
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AppSettings.Where(x => x.Key == key).ExecuteDeleteAsync(ct) > 0;
    }
}
