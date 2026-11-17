using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.ApiKeys;

public sealed record CreatedApiKey(ApiKey Key, string PlainText);

/// <summary>
/// Klucze <c>mon_&lt;prefiks&gt;_&lt;sekret&gt;</c>: sekret to 32 losowe bajty, w bazie prefiks i SHA-256 sekretu
/// (przy tak losowym sekrecie PBKDF2 nie jest potrzebne). Pełny klucz widać tylko raz, przy utworzeniu.
/// </summary>
public sealed class ApiKeyService(IDbContextFactory<ZdrowieInfoDbContext> dbFactory, TimeProvider time)
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public async Task<CreatedApiKey> CreateAsync(string name, IEnumerable<string> scopes, CancellationToken ct = default)
    {
        var validScopes = scopes.Where(ApiScopes.All.Contains).Distinct().ToList();
        if (validScopes.Count == 0) throw new ArgumentException("Klucz musi mieć co najmniej jeden zakres.");

        var prefix = RandomNumberGenerator.GetString(Alphabet, 8);
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            Name = name.Trim(),
            Prefix = prefix,
            SecretHash = Hash(secret),
            Scopes = validScopes,
            CreatedAt = time.GetUtcNow(),
        };
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return new CreatedApiKey(key, $"mon_{prefix}_{secret}");
    }

    /// <summary>Sprawdza klucz z nagłówka; null, gdy nieprawidłowy albo unieważniony.</summary>
    public async Task<ApiKey?> ValidateAsync(string plainText, CancellationToken ct = default)
    {
        var parts = plainText.Split('_', 3);
        if (parts.Length != 3 || parts[0] != "mon" || parts[1].Length != 8 || parts[2].Length < 40) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Prefix == parts[1], ct);
        if (key is null || key.RevokedAt is not null) return null;

        var expected = Convert.FromHexString(key.SecretHash);
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(parts[2]));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

        // Ostatnie użycie najwyżej raz na minutę – bez zapisu przy każdym żądaniu.
        var now = time.GetUtcNow();
        if (key.LastUsedAt is null || now - key.LastUsedAt > TimeSpan.FromMinutes(1))
            await db.ApiKeys.Where(k => k.Id == key.Id).ExecuteUpdateAsync(u => u.SetProperty(k => k.LastUsedAt, now), ct);
        return key;
    }

    public async Task<List<ApiKey>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
    }

    public async Task RevokeAsync(long id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ApiKeys.Where(k => k.Id == id && k.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAt, time.GetUtcNow()), ct);
    }

    internal static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
