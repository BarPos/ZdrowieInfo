using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Sources;

namespace ZdrowieInfo.Server.Entries;

/// <param name="Origin">Skąd pochodzi komunikat, np. „CSIRT MON”.</param>
/// <param name="Html">Treść wklejona w edytorze (oczyszczana przy zapisie).</param>
public sealed record UserEntryInput(string Origin, string Title, string Html, DateOnly PublishedOn);

public sealed record UserEntryResult(bool Ok, long? Id = null, string? Error = null);

/// <summary>
/// Wpisy własne administratora (np. jawne komunikaty CSIRT MON z poczty): zwykłe wpisy jednego ukrytego źródła
/// systemowego, więc trafiają do raportu tak jak wpisy ze stron (czekające, zaraportowane, sprawdzanie cytatów).
/// Każdy ma własny adres w aplikacji (link w raporcie i PDF). Edycja i usuwanie – tylko przed raportem.
/// </summary>
public sealed class UserEntryService(IDbContextFactory<ZdrowieInfoDbContext> dbFactory, IOptions<ZdrowieInfoOptions> app, TimeProvider time)
{
    /// <summary>Źródło systemowe wpisów własnych – nieaktywne i „ręczne”, więc nie jest sprawdzane ani pokazywane w stanie źródeł.</summary>
    public const string SourceSlug = "wpisy-wlasne";
    public const string SourceName = "Wpisy własne";

    /// <summary>Etykieta źródła wpisu w raporcie: „CSIRT MON (wpis własny)”.</summary>
    public static string Label(string origin) => $"{origin} (wpis własny)";

    public string PageUrl(long id) => $"{app.Value.PublicUrl.TrimEnd('/')}/wpisy-wlasne/{id}";

    /// <param name="key">Klucz z identyfikatora maila (import): wpis o tym kluczu już istnieje – nic nie jest dodawane.</param>
    public async Task<UserEntryResult> CreateAsync(UserEntryInput input, string createdBy, CancellationToken ct = default, string? key = null)
    {
        if (Validate(input, out var html, out var text) is { } error) return new UserEntryResult(false, Error: error);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sourceId = await EnsureSourceAsync(db, ct);
        if (key is not null && await db.SourceItems.AnyAsync(i => i.SourceId == sourceId && i.Key == key, ct))
            return new UserEntryResult(false, Error: "Ten mail jest już zaimportowany.");
        var now = time.GetUtcNow();
        var item = new SourceItem
        {
            SourceId = sourceId,
            Kind = SourceItemKind.Entry,
            Key = key ?? $"wpis-wlasny:{Guid.NewGuid():N}",
            Url = "", // adres z identyfikatorem – po zapisie
            Title = input.Title.Trim(),
            PublishedOn = input.PublishedOn,
            Content = text,
            ContentHash = Hash(text),
            ContentHtml = html,
            Origin = input.Origin.Trim(),
            CreatedBy = createdBy,
            FirstSeenAt = now,
            LastChangedAt = now,
            DetailFetchedAt = now, // treść jest – aplikacja nie próbuje jej pobierać
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.SourceItems.Add(item);
        await db.SaveChangesAsync(ct);
        item.Url = PageUrl(item.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new UserEntryResult(true, item.Id);
    }

    public async Task<UserEntryResult> UpdateAsync(long id, UserEntryInput input, CancellationToken ct = default)
    {
        if (Validate(input, out var html, out var text) is { } error) return new UserEntryResult(false, id, error);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await Mine(db).SingleOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return new UserEntryResult(false, id, "Nie ma takiego wpisu.");
        if (item.ReportedAt is not null) return new UserEntryResult(false, id, "Wpis jest już w raporcie – nie można go zmienić.");
        item.Origin = input.Origin.Trim();
        item.Title = input.Title.Trim();
        item.PublishedOn = input.PublishedOn;
        item.ContentHtml = html;
        item.Content = text;
        item.ContentHash = Hash(text);
        item.LastChangedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new UserEntryResult(true, id);
    }

    public async Task<UserEntryResult> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await Mine(db).SingleOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return new UserEntryResult(false, id, "Nie ma takiego wpisu.");
        if (item.ReportedAt is not null) return new UserEntryResult(false, id, "Wpis jest już w raporcie – nie można go usunąć.");
        db.SourceItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return new UserEntryResult(true, id);
    }

    /// <summary>
    /// Wpis z raportu wraca do stanu „czeka na raport”: trafi do najbliższego raportu dziennego, znów można go zmienić
    /// albo usunąć. Raport, w którym był, zostaje bez zmian (cytaty mają własną kopię tytułu i fragmentu).
    /// </summary>
    public async Task<UserEntryResult> ReleaseAsync(long id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await Mine(db).SingleOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return new UserEntryResult(false, id, "Nie ma takiego wpisu.");
        if (item.ReportedAt is null) return new UserEntryResult(false, id, "Wpis już czeka na raport.");
        item.ReportedAt = null;
        item.ReportId = null;
        item.LastChangedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new UserEntryResult(true, id);
    }

    /// <summary>Które z kluczy (maili) są już zaimportowane – do podglądu importu.</summary>
    public async Task<HashSet<string>> ExistingKeysAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = keys.ToList();
        return (await Mine(db).Where(i => list.Contains(i.Key)).Select(i => i.Key).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Wpisy własne (do listy w panelu i strony wpisu).</summary>
    public static IQueryable<SourceItem> Mine(ZdrowieInfoDbContext db) =>
        from i in db.SourceItems
        join s in db.Sources on i.SourceId equals s.Id
        where s.Slug == SourceSlug
        select i;

    private static string? Validate(UserEntryInput input, out string html, out string text)
    {
        html = text = "";
        if (string.IsNullOrWhiteSpace(input.Origin)) return "Podaj źródło komunikatu (np. CSIRT MON).";
        if (input.Origin.Trim().Length > 200) return "Źródło może mieć najwyżej 200 znaków.";
        if (string.IsNullOrWhiteSpace(input.Title)) return "Podaj tytuł komunikatu.";
        if (input.Title.Trim().Length > 500) return "Tytuł może mieć najwyżej 500 znaków.";
        if ((input.Html?.Length ?? 0) > RichText.MaxHtmlChars) return $"Treść jest za długa (najwyżej {RichText.MaxHtmlChars / 1000} tys. znaków HTML).";
        html = RichText.Sanitize(input.Html);
        text = RichText.ToText(html);
        if (text.Length == 0) return "Wklej albo wpisz treść komunikatu.";
        return null;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private async Task<int> EnsureSourceAsync(ZdrowieInfoDbContext db, CancellationToken ct)
    {
        var id = await db.Sources.Where(s => s.Slug == SourceSlug).Select(s => (int?)s.Id).SingleOrDefaultAsync(ct);
        if (id is { } existing) return existing;
        var now = DateTimeOffset.UtcNow;
        var source = new Source
        {
            Slug = SourceSlug,
            Name = SourceName,
            Url = $"{app.Value.PublicUrl.TrimEnd('/')}/panel/wpisy-wlasne",
            Method = SourceMethod.Page,
            FetchMode = FetchMode.Manual,
            IsActive = false,
            Status = SourceStatus.Ok,
            Notes = "Źródło systemowe wpisów dodawanych ręcznie w panelu (Wpisy własne). Nie jest sprawdzane.",
            BaselineAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Sources.Add(source);
        try
        {
            await db.SaveChangesAsync(ct);
            return source.Id;
        }
        catch (DbUpdateException)
        {
            // Równoległy zapis utworzył źródło pierwszy (unikalny slug).
            db.Entry(source).State = EntityState.Detached;
            return await db.Sources.Where(s => s.Slug == SourceSlug).Select(s => s.Id).SingleAsync(ct);
        }
    }
}
