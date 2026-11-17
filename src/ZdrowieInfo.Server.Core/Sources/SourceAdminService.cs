using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Sources.Extraction;
using ZdrowieInfo.Server.Sources.Fetching;

namespace ZdrowieInfo.Server.Sources;

/// <summary>Podgląd działania selektorów na żywej stronie (bez zapisu).</summary>
public sealed record SelectorPreview(bool Success, string? Error, IReadOnlyList<ExtractedItem> Items, string? PageText, string? DetailText, string? DetailUrl, string? NextPageUrl = null);

/// <summary>Edycja źródeł z panelu.</summary>
public sealed class SourceAdminService(IDbContextFactory<ZdrowieInfoDbContext> dbFactory, SourceFetcher fetcher, TimeProvider time)
{
    public sealed record SourceEdit(string Name, string Url, SourceGroup Group, SourceMethod Method, FetchMode FetchMode, bool IsActive, string? Notes, SourceSelectors Selectors);

    /// <summary>
    /// Zapis zmian. Zmiana adresu, metody albo selektorów oznacza nowy stan bazowy: inaczej wpisy, które „pojawiły się”
    /// tylko dlatego, że selektor łapie teraz coś innego, trafiłyby do raportu jako nowe. Czyści też walidatory HTTP.
    /// </summary>
    public async Task<bool> UpdateAsync(int id, SourceEdit edit, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.Sources.SingleAsync(s => s.Id == id, ct);

        var rebaseline = source.Url != edit.Url || source.Method != edit.Method || !SameSelectors(source.Selectors, edit.Selectors);
        source.Name = edit.Name.Trim();
        source.Url = edit.Url.Trim();
        source.Group = edit.Group;
        source.Method = edit.Method;
        source.FetchMode = edit.FetchMode;
        source.IsActive = edit.IsActive;
        source.Notes = string.IsNullOrWhiteSpace(edit.Notes) ? null : edit.Notes.Trim();
        source.Selectors = edit.Selectors;
        source.UpdatedAt = time.GetUtcNow();

        if (rebaseline)
        {
            source.BaselineAt = null;
            source.ArchiveFrom = null; // inny adres albo selektory – przejrzane archiwum nieaktualne
            source.ETag = null;
            source.LastModified = null;
            source.ContentHash = null;
            source.PageText = null;
            source.NextRetryAt = null;
            source.ConsecutiveFailures = 0;
            source.FailingSince = null;
        }
        await db.SaveChangesAsync(ct);
        return rebaseline;
    }

    public async Task SetActiveAsync(int id, bool active, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Sources.Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.IsActive, active).SetProperty(s => s.UpdatedAt, time.GetUtcNow()), ct);
    }

    /// <summary>Pobiera stronę na żywo i pokazuje, co znajdują podane selektory; dla listy także treść pierwszego wpisu.</summary>
    public async Task<SelectorPreview> PreviewAsync(string url, SourceMethod method, SourceSelectors selectors, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            return new SelectorPreview(false, "Nieprawidłowy adres strony.", [], null, null, null);

        var fetch = await fetcher.FetchAsync(url, ct: ct);
        if (!fetch.Success || fetch.Html is null)
            return new SelectorPreview(false, fetch.Message ?? "Brak treści.", [], null, null, null);

        try
        {
            if (method == SourceMethod.Page)
            {
                var text = SourceExtractor.ExtractPageText(fetch.Html, url, selectors);
                return text is null
                    ? new SelectorPreview(false, "Selektor treści nic nie znalazł.", [], null, null, null)
                    : new SelectorPreview(true, null, [], text, null, null);
            }

            var list = SourceExtractor.ExtractList(fetch.Html, url, selectors);
            if (list.Items.Count == 0)
                return new SelectorPreview(false, "Selektor nie znalazł żadnych wpisów.", [], null, null, null);

            var first = list.Items[0];
            var detail = await fetcher.FetchAsync(first.Url, ct: ct);
            var detailText = detail.Success && detail.Html is not null ? SourceExtractor.ExtractDetailText(detail.Html, first.Url, selectors) : null;
            return new SelectorPreview(true, list.Skipped > 0 ? $"{list.Skipped} elementów pominiętych (bez linku albo tytułu)." : null,
                list.Items, null, detailText ?? detail.Message, first.Url, list.NextPageUrl);
        }
        catch (SelectorException e)
        {
            return new SelectorPreview(false, e.Message, [], null, null, null);
        }
    }

    /// <summary>
    /// Przywraca konfigurację z DefaultSources.json (adres, metodę, częstotliwość, selektory) dla jednego albo wszystkich źródeł.
    /// Zmiana selektorów oznacza nowy stan bazowy (jak przy edycji w panelu). Zwraca slugi zmienionych źródeł.
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplyDefaultsAsync(string? slug, CancellationToken ct = default)
    {
        var defaults = DefaultSource.Load().Where(d => slug is null || d.Slug == slug).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sources = await db.Sources.AsNoTracking().ToDictionaryAsync(s => s.Slug, ct);
        var changed = new List<string>();
        foreach (var d in defaults)
        {
            if (!sources.TryGetValue(d.Slug, out var s)) continue;
            if (s.Url == d.Url && s.Method == d.Method && s.Group == d.Group && SameSelectors(s.Selectors, d.Selectors)) continue;
            await UpdateAsync(s.Id, new SourceEdit(d.Name, d.Url, d.Group, d.Method, s.FetchMode, s.IsActive, d.Notes, d.Selectors), ct);
            changed.Add(d.Slug);
        }
        return changed;
    }

    private static bool SameSelectors(SourceSelectors a, SourceSelectors b) =>
        System.Text.Json.JsonSerializer.Serialize(a, DefaultSource.Json) == System.Text.Json.JsonSerializer.Serialize(b, DefaultSource.Json);
}
