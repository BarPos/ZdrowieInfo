using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Sources;
using ZdrowieInfo.Server.Sources.Checking;
using ZdrowieInfo.Server.Sources.Extraction;
using ZdrowieInfo.Server.Sources.Fetching;

namespace ZdrowieInfo.Server.Hosting;

/// <summary>Polecenia serwera do strojenia źródeł na serwerze.</summary>
internal static class SourceCli
{
    /// <summary>--check-sources: pobiera strony na żywo i pokazuje, co znajdują selektory. Nic nie zapisuje.</summary>
    public static async Task<int> TrySelectorsAsync(IServiceProvider services, string? slug)
    {
        var sources = await LoadAsync(services, slug);
        if (sources.Count == 0) return NotFound(slug);
        var fetcher = services.GetRequiredService<SourceFetcher>();
        var failures = 0;

        foreach (var source in sources)
        {
            Console.WriteLine($"== {source.Slug} – {source.Name}");
            var fetch = await fetcher.FetchAsync(source.Url);
            if (!fetch.Success)
            {
                Console.WriteLine($"   BŁĄD {fetch.Error}: {fetch.Message}");
                failures++;
                continue;
            }
            try
            {
                if (source.Method == SourceMethod.List)
                {
                    var result = SourceExtractor.ExtractList(fetch.Html!, source.Url, source.Selectors);
                    Console.WriteLine($"   {result.Items.Count} wpisów" + (result.Skipped > 0 ? $", {result.Skipped} pominiętych (bez linku/tytułu)" : ""));
                    foreach (var item in result.Items.Take(3))
                        Console.WriteLine($"   {item.PublishedOn:yyyy-MM-dd} {item.Title}");
                    if (result.NextPageUrl is not null) Console.WriteLine($"   następna strona: {result.NextPageUrl}");
                    else if (source.Selectors.NextPage is not null) Console.WriteLine("   następna strona: BRAK (selektor NextPage nic nie znalazł)");
                    if (source.Selectors.OverflowUrl is not null) Console.WriteLine($"   lista zapasowa: {source.Selectors.OverflowUrl}");
                    if (result.Items.Count == 0) failures++;
                }
                else
                {
                    var text = SourceExtractor.ExtractPageText(fetch.Html!, source.Url, source.Selectors);
                    Console.WriteLine(text is null ? "   BRAK TREŚCI" : $"   {text.Length} znaków, hash {SourceExtractor.Hash(text)[..12]}");
                    if (text is null) failures++;
                }
            }
            catch (SelectorException e)
            {
                Console.WriteLine($"   BŁĄD selektora: {e.Message}");
                failures++;
            }
        }
        Console.WriteLine(failures == 0 ? "Wszystkie źródła OK." : $"Źródła z problemem: {failures}.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>--check-now: prawdziwe sprawdzenie (zapis wpisów, stan bazowy, incydenty).</summary>
    public static async Task<int> CheckNowAsync(IServiceProvider services, string? slug)
    {
        var sources = await LoadAsync(services, slug);
        if (sources.Count == 0) return NotFound(slug);
        var kind = sources.All(s => s.BaselineAt is null) ? CheckRunKind.Baseline : CheckRunKind.Manual;
        var result = await services.GetRequiredService<SourceCheckService>().RunAsync(sources.Select(s => s.Id).ToList(), kind);
        Console.WriteLine($"Uruchomienie {result.RunId}: {result.SourcesChecked} źródeł, {result.SourcesFailed} z błędem, {result.NewItems} nowych/zmienionych wpisów.");
        return result.SourcesFailed == 0 ? 0 : 1;
    }

    /// <summary>--apply-default-sources: konfiguracja z DefaultSources.json (po aktualizacji aplikacji z poprawionymi selektorami).</summary>
    public static async Task<int> ApplyDefaultsAsync(IServiceProvider services, string? slug)
    {
        var changed = await services.GetRequiredService<SourceAdminService>().ApplyDefaultsAsync(slug);
        Console.WriteLine(changed.Count == 0
            ? "Konfiguracja źródeł jest zgodna z listą domyślną."
            : $"Zaktualizowano ({changed.Count}): {string.Join(", ", changed)}. Następne sprawdzenie zapisze dla nich nowy stan bazowy.");
        return 0;
    }

    private static async Task<List<Source>> LoadAsync(IServiceProvider services, string? slug)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContextAsync();
        return await db.Sources.AsNoTracking()
            .Where(s => slug == null ? s.IsActive && s.FetchMode != FetchMode.Manual : s.Slug == slug)
            .OrderBy(s => s.Id)
            .ToListAsync();
    }

    private static int NotFound(string? slug)
    {
        Console.Error.WriteLine(slug is null ? "Brak aktywnych źródeł." : $"Nie ma źródła {slug}.");
        return 2;
    }
}
