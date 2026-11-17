using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Sources;

/// <summary>Domyślna lista źródeł (osadzony DefaultSources.json).</summary>
public sealed record DefaultSource(string Slug, string Name, string Url, SourceGroup Group, SourceMethod Method, string? Notes, SourceSelectors Selectors)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static IReadOnlyList<DefaultSource> Load()
    {
        using var stream = typeof(DefaultSource).Assembly.GetManifestResourceStream("ZdrowieInfo.Server.Sources.DefaultSources.json")
            ?? throw new InvalidOperationException("Brak zasobu DefaultSources.json.");
        return JsonSerializer.Deserialize<List<DefaultSource>>(stream, Json)!;
    }
}

/// <summary>Dodaje brakujące źródła (po slugu). Istniejących nie zmienia – edycje z panelu zostają.</summary>
public sealed class SourceSeeder(IDbContextFactory<ZdrowieInfoDbContext> dbFactory, TimeProvider time, ILogger<SourceSeeder> logger)
{
    public async Task<int> SeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = (await db.Sources.Select(s => s.Slug).ToListAsync(ct)).ToHashSet();
        var now = time.GetUtcNow();
        var added = 0;
        foreach (var d in DefaultSource.Load().Where(d => !existing.Contains(d.Slug)))
        {
            db.Sources.Add(new Source
            {
                Slug = d.Slug, Name = d.Name, Url = d.Url, Group = d.Group, Method = d.Method, Notes = d.Notes,
                Selectors = d.Selectors, CreatedAt = now, UpdatedAt = now,
            });
            added++;
        }
        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Dodano {Count} źródeł z listy domyślnej.", added);
        }
        return added;
    }
}
