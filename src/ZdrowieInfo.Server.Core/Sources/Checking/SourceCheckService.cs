using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Push;
using ZdrowieInfo.Server.Sources.Extraction;
using ZdrowieInfo.Server.Sources.Fetching;

namespace ZdrowieInfo.Server.Sources.Checking;

public sealed record CheckRunResult(long RunId, int SourcesChecked, int SourcesFailed, int NewItems);

/// <summary>Wynik pobrania archiwum list (raport wstecz): ile źródeł przejrzano, ile wpisów dodano, co się nie udało.</summary>
public sealed record ArchiveResult(int SourcesFetched, int ItemsAdded, IReadOnlyList<string> Warnings);

/// <summary>
/// Sprawdzenie źródeł: pobranie, ekstrakcja, porównanie z bazą, stan bazowy, treść nowych wpisów, incydenty.
/// Zmiany wykrywa kod – Claude dostaje (w etapie 3) tylko to, co tu zapisano jako nowe.
/// </summary>
public sealed class SourceCheckService(
    IDbContextFactory<ZdrowieInfoDbContext> dbFactory,
    SourceFetcher fetcher,
    TimeProvider time,
    WarsawClock clock,
    IOptions<SourcesOptions> options,
    NotificationDispatcher notifications,
    ILogger<SourceCheckService> logger)
{
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>Trwa sprawdzanie źródeł albo pobieranie archiwum (aktualizacja serwera czeka).</summary>
    public bool IsBusy => _runLock.CurrentCount == 0;

    private sealed record Outcome(bool Success, int NewItems = 0, CheckErrorType? Error = null, string? Message = null, int? ItemsFound = null,
        string? ContentHash = null, int Pages = 1, string? Warning = null)
    {
        public static Outcome Fail(CheckErrorType error, string message) => new(false, Error: error, Message: message);
    }

    /// <summary>Jedno uruchomienie naraz (harmonogram, ponowienia i „Sprawdź teraz” czekają na siebie).</summary>
    public async Task<CheckRunResult> RunAsync(IReadOnlyCollection<int> sourceIds, CheckRunKind kind, CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var sources = await db.Sources.AsNoTracking().Where(s => sourceIds.Contains(s.Id)).ToListAsync(ct);
            var run = new CheckRun
            {
                Date = clock.Today,
                Kind = kind,
                Groups = sources.Select(s => s.Group).Distinct().Order().ToList(),
                StartedAt = time.GetUtcNow(),
                Status = CheckRunStatus.Running,
            };
            db.CheckRuns.Add(run);
            await db.SaveChangesAsync(ct);

            var details = new ConcurrentDictionary<string, string?>(); // treść wpisów pobrana w tym uruchomieniu (te same URL-e w kilku źródłach)
            try
            {
                // Domeny równolegle, w obrębie domeny po kolei (odstępy pilnuje HostThrottle).
                var outcomes = await Task.WhenAll(sources
                    .GroupBy(s => SourceFetcher.HostOf(s.Url))
                    .Select(async host =>
                    {
                        var list = new List<Outcome>();
                        foreach (var source in host) list.Add(await CheckSourceAsync(source.Id, run.Id, details, ct));
                        return list;
                    }));
                var flat = outcomes.SelectMany(o => o).ToList();
                run.SourcesChecked = flat.Count;
                run.SourcesFailed = flat.Count(o => !o.Success);
                run.NewItems = flat.Sum(o => o.NewItems);
                run.Status = CheckRunStatus.Completed;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Uruchomienie {RunId} przerwane błędem.", run.Id);
                run.Status = CheckRunStatus.Failed;
                run.Error = e.Message;
            }
            finally
            {
                run.FinishedAt = time.GetUtcNow();
                db.CheckRuns.Update(run);
                await db.SaveChangesAsync(CancellationToken.None);
            }

            logger.LogInformation("Uruchomienie {RunId} ({Kind}): {Checked} źródeł, {Failed} z błędem, {New} nowych wpisów.",
                run.Id, kind, run.SourcesChecked, run.SourcesFailed, run.NewItems);
            // Otwarte i zamknięte incydenty – powiadomienie od razu (flagi w bazie chronią przed duplikatami).
            await notifications.DispatchPendingAsync(CancellationToken.None);
            return new CheckRunResult(run.Id, run.SourcesChecked, run.SourcesFailed, run.NewItems);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<Outcome> CheckSourceAsync(int sourceId, long runId, ConcurrentDictionary<string, string?> details, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.Sources.SingleAsync(s => s.Id == sourceId, ct);
        var now = time.GetUtcNow();
        var snapshot = new SourceSnapshot { SourceId = source.Id, CheckRunId = runId, FetchedAt = now };

        Outcome outcome;
        try
        {
            // Walidatory HTTP tylko po stanie bazowym (inaczej 304 ukryłby np. zmieniony selektor) i tylko dla metody
            // „cała strona”. Listy zawsze pobieramy w całości: to jedno żądanie, a nie mamy dowodu, że ETag np. gov.pl
            // zmienia się razem z listą – 304 w złym momencie ukryłby nowe wpisy.
            var useValidators = source.BaselineAt is not null && source.Method == SourceMethod.Page;
            var fetch = await fetcher.FetchAsync(source.Url, useValidators ? source.ETag : null, useValidators ? source.LastModified : null, ct);
            snapshot.HttpStatus = fetch.Status;

            if (!fetch.Success)
            {
                outcome = Outcome.Fail(fetch.Error!.Value, fetch.Message!);
            }
            else if (fetch.NotModified)
            {
                snapshot.NotModified = true;
                outcome = new Outcome(true, ContentHash: source.ContentHash);
            }
            else
            {
                outcome = source.Method == SourceMethod.List
                    ? await ProcessListAsync(db, source, fetch.Html!, now, ct)
                    : ProcessPage(db, source, fetch.Html!, now);
                if (outcome.Success)
                {
                    source.ETag = fetch.ETag;
                    source.LastModified = fetch.LastModified;
                }
            }
        }
        catch (SelectorException e)
        {
            outcome = Outcome.Fail(CheckErrorType.NoItems, e.Message);
        }

        if (outcome.Success) await MarkSuccessAsync(db, source, now, ct);
        else await MarkFailureAsync(db, source, outcome.Error!.Value, outcome.Message!, now, ct);

        snapshot.Succeeded = outcome.Success;
        snapshot.ErrorType = outcome.Error;
        snapshot.ErrorMessage = outcome.Message;
        snapshot.ItemsFound = outcome.ItemsFound;
        snapshot.NewItems = outcome.NewItems;
        snapshot.ContentHash = outcome.ContentHash;
        snapshot.PagesFetched = outcome.Pages;
        snapshot.Warning = outcome.Warning;
        if (outcome.Warning is not null) source.StatusMessage = outcome.Warning;
        snapshot.DurationMs = (int)stopwatch.ElapsedMilliseconds;
        db.SourceSnapshots.Add(snapshot);
        source.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        // Treść nowych wpisów dopiero po zapisaniu ich listy: przerwane pobieranie dokończy następne sprawdzenie.
        if (outcome.Success && source.Method == SourceMethod.List)
            await FetchDetailsAsync(db, source, details, ct);

        if (!outcome.Success)
            logger.LogWarning("Źródło {Slug}: {Error} – {Message}", source.Slug, outcome.Error, outcome.Message);
        else if (outcome.Warning is not null)
            logger.LogWarning("Źródło {Slug}: {Warning}", source.Slug, outcome.Warning);
        return outcome;
    }

    private async Task<Outcome> ProcessListAsync(ZdrowieInfoDbContext db, Source source, string html, DateTimeOffset now, CancellationToken ct)
    {
        var extraction = SourceExtractor.ExtractList(html, source.Url, source.Selectors);
        if (extraction.Items.Count == 0)
            return Outcome.Fail(CheckErrorType.NoItems, "Selektor nie znalazł żadnych wpisów – strona mogła zmienić układ.");

        var baseline = source.BaselineAt is null;
        // Stan bazowy tylko z pierwszej strony – archiwum nie jest potrzebne.
        var (found, pages, warning, failure) = baseline
            ? (extraction.Items.ToList(), 1, (string?)null, (Outcome?)null)
            : await CollectPagesAsync(db, source, extraction, now, ct);
        // Kolejna strona się nie pobrała: nic nie zapisujemy – ponowienie za 5 min przejdzie listę od początku.
        // (Zapis części wpisów zostawiłby trwałą lukę: następnym razem ostatni wpis 1. strony byłby już znany.)
        if (failure is not null) return failure;

        var keys = found.Select(i => i.Key).ToList();
        var existing = await db.SourceItems.Where(i => i.SourceId == source.Id && keys.Contains(i.Key)).ToDictionaryAsync(i => i.Key, ct);
        var changes = 0;

        foreach (var item in found)
        {
            if (existing.TryGetValue(item.Key, out var known))
            {
                // NFZ podbija stare wpisy zmieniając tytuł („[AKTUALIZACJA…]”) albo datę – to aktualizacja do raportu.
                var titleChanged = known.Title != item.Title;
                var dateChanged = item.PublishedOn is not null && known.PublishedOn != item.PublishedOn;
                if (baseline || (!titleChanged && !dateChanged)) continue;

                known.PreviousTitle = known.Title;
                known.Title = item.Title;
                known.PublishedOn = item.PublishedOn ?? known.PublishedOn;
                known.Summary = item.Summary ?? known.Summary;
                known.UpdateCount++;
                known.LastChangedAt = now;
                known.IsBaseline = false;
                known.DetailFetchedAt = null;
                known.DetailAttempts = 0;
                known.DetailError = null;
                known.ReportedAt = null; // aktualizacja wraca do najbliższego raportu
                known.ReportId = null;
                changes++;
                continue;
            }

            db.SourceItems.Add(new SourceItem
            {
                SourceId = source.Id,
                Kind = SourceItemKind.Entry,
                Key = item.Key,
                Url = item.Url,
                Title = item.Title,
                PublishedOn = item.PublishedOn,
                Summary = item.Summary,
                IsBaseline = baseline,
                FirstSeenAt = now,
                LastChangedAt = now,
            });
            if (!baseline) changes++;
        }

        if (baseline)
        {
            source.BaselineAt = now;
            logger.LogInformation("Źródło {Slug}: stan bazowy ({Count} wpisów).", source.Slug, extraction.Items.Count);
        }
        return new Outcome(true, changes, ItemsFound: found.Count, Pages: pages, Warning: warning);
    }

    /// <summary>
    /// Gdy ostatni (najstarszy) wpis na stronie jest nowy, za nim mogą być kolejne nowe – przechodzimy na następne strony,
    /// aż trafimy na znany wpis albo na wpisy starsze niż ostatnie udane sprawdzenie (minus dzień zapasu). Jeśli lista nie ma kolejnych stron (blok „10 najnowszych”), używamy listy zapasowej
    /// (OverflowUrl) i bierzemy z niej wpisy od daty ostatniego udanego sprawdzenia. Gdy nie da się dojść do znanego
    /// wpisu w limicie stron – ostrzeżenie zamiast cichej utraty.
    /// </summary>
    private async Task<(List<ExtractedItem> Items, int Pages, string? Warning, Outcome? Failure)> CollectPagesAsync(
        ZdrowieInfoDbContext db, Source source, ListExtraction first, DateTimeOffset now, CancellationToken ct)
    {
        var max = Math.Max(1, options.Value.MaxListPages);
        var items = new List<ExtractedItem>(first.Items);
        var seen = items.Select(i => i.Key).ToHashSet();
        var current = first;
        var pages = 1;
        var overflow = false;
        var cutoff = DateOnly.FromDateTime(WarsawClock.ToLocal(source.LastSuccessAt ?? source.BaselineAt ?? now)).AddDays(-1);

        while (true)
        {
            var dates = current.Items.Where(i => i.PublishedOn is not null).Select(i => i.PublishedOn!.Value).ToList();
            // Strona sięga przed ostatnie udane sprawdzenie: dalej są tylko starsze wpisy. Chroni też przed zalaniem raportu,
            // gdy strona usunie wpis albo zmieni adresy (wtedy „nowy” ostatni wpis to stary wpis z kolejnej strony).
            if (dates.Count > 0 && dates.Min() < cutoff) return (items, pages, null, null);
            if (!overflow)
            {
                var last = current.Items[^1].Key;
                if (await db.SourceItems.AnyAsync(i => i.SourceId == source.Id && i.Key == last, ct)) return (items, pages, null, null);
            }

            var nextUrl = current.NextPageUrl;
            if (nextUrl is null && !overflow && source.Selectors.OverflowUrl is { } overflowUrl)
            {
                nextUrl = overflowUrl;
                overflow = true;
            }
            if (nextUrl is null)
                return (items, pages, pages == 1 && source.Selectors.NextPage is null && source.Selectors.OverflowUrl is null
                    ? "Wszystkie wpisy na liście są nowe, a źródło nie ma kolejnych stron (NextPage) – część wpisów mogła zostać pominięta."
                    : $"Wszystkie wpisy na {pages} stronach są nowe i nie ma kolejnej strony – część wpisów mogła zostać pominięta.", null);
            if (pages >= max)
                return (items, pages, $"Przejrzano {pages} stron listy i wszystkie wpisy są nowe – część wpisów mogła zostać pominięta (limit MaxListPages).", null);

            var fetch = await fetcher.FetchAsync(nextUrl, ct: ct);
            pages++;
            if (!fetch.Success || fetch.Html is null)
                return (items, pages, null, Outcome.Fail(fetch.Error ?? CheckErrorType.Http,
                    $"Nie udało się pobrać {pages}. strony listy ({fetch.Message}) – sprawdzenie zostanie powtórzone."));

            try
            {
                current = SourceExtractor.ExtractList(fetch.Html, nextUrl, source.Selectors);
            }
            catch (SelectorException e)
            {
                return (items, pages, null, Outcome.Fail(CheckErrorType.NoItems, $"{pages}. strona listy: {e.Message}"));
            }
            if (current.Items.Count == 0)
                return (items, pages, null, Outcome.Fail(CheckErrorType.NoItems, $"{pages}. strona listy nie ma wpisów – sprawdzenie zostanie powtórzone."));

            foreach (var item in current.Items)
            {
                if (overflow && item.PublishedOn is { } date && date < cutoff) continue;
                if (seen.Add(item.Key)) items.Add(item);
            }
            logger.LogInformation("Źródło {Slug}: strona {Page} listy{Overflow} ({Url}).", source.Slug, pages, overflow ? " zapasowej" : "", nextUrl);
        }
    }

    private Outcome ProcessPage(ZdrowieInfoDbContext db, Source source, string html, DateTimeOffset now)
    {
        var text = SourceExtractor.ExtractPageText(html, source.Url, source.Selectors);
        if (string.IsNullOrWhiteSpace(text))
            return Outcome.Fail(CheckErrorType.NoItems, "Selektor treści nic nie znalazł – strona mogła zmienić układ.");

        var hash = SourceExtractor.Hash(text);
        if (source.BaselineAt is null)
        {
            source.BaselineAt = now;
            source.ContentHash = hash;
            source.PageText = text;
            logger.LogInformation("Źródło {Slug}: stan bazowy treści strony.", source.Slug);
            return new Outcome(true, ItemsFound: 1, ContentHash: hash);
        }
        if (hash == source.ContentHash)
            return new Outcome(true, ItemsFound: 1, ContentHash: hash);

        var local = WarsawClock.ToLocal(now);
        db.SourceItems.Add(new SourceItem
        {
            SourceId = source.Id,
            Kind = SourceItemKind.PageChange,
            Key = $"{UrlNormalizer.Key(source.Url)}#{local:yyyy-MM-ddTHH:mm:ss}",
            Url = source.Url,
            Title = $"Zmiana strony: {source.Name}",
            PublishedOn = DateOnly.FromDateTime(local),
            Content = PageDiff.Build(source.PageText ?? "", text),
            ContentHash = hash,
            FirstSeenAt = now,
            LastChangedAt = now,
            DetailFetchedAt = now,
        });
        source.ContentHash = hash;
        source.PageText = text;
        return new Outcome(true, 1, ItemsFound: 1, ContentHash: hash);
    }

    /// <summary>
    /// Raport wstecz i okresowy: dla każdego aktywnego źródła-listy, którego wpisów od <paramref name="from"/> jeszcze nie znamy
    /// (dzień sprzed stanu bazowego i sprzed dotychczas przejrzanego archiwum), przechodzi kolejne strony listy (NextPage, potem
    /// lista zapasowa), aż dojdzie do wpisów sprzed tej daty. Zasięg zapisuje w źródle – następny raz bez pobierania.
    /// Nowe wpisy zapisuje jako zastane (IsBaseline) – nie trafiają do raportów dziennych, tylko do raportów za dni, w których
    /// je opublikowano. Nie zmienia stanu źródła ani incydentów. Strony „cała strona” nie mają archiwum.
    /// </summary>
    public async Task<ArchiveResult> EnsureArchiveAsync(DateOnly from, CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var sources = await db.Sources.AsNoTracking().Where(s => s.IsActive && s.Method == SourceMethod.List).OrderBy(s => s.Name).ToListAsync(ct);

            var warnings = new List<string>();
            int fetched = 0, added = 0;
            foreach (var source in sources)
            {
                // Od stanu bazowego zwykłe sprawdzenia widzą każdy nowy wpis (przy przerwach – przechodzą kolejne strony),
                // a wcześniejsze dni – tylko jeśli archiwum już przejrzano. Najstarszy znany wpis nie wystarcza
                // (przypięty stary artykuł na pierwszej stronie udawałby pełne pokrycie).
                var since = source.BaselineAt is { } baseline ? DateOnly.FromDateTime(WarsawClock.ToLocal(baseline)) : (DateOnly?)null;
                if (since <= from || source.ArchiveFrom <= from) continue;
                fetched++;
                var (count, reached, warning) = await WalkArchiveAsync(db, source, from, ct);
                added += count;
                if (warning is not null) warnings.Add($"{source.Name}: {warning}");
                if (reached is { } r && (source.ArchiveFrom is null || r < source.ArchiveFrom))
                    await db.Sources.Where(s => s.Id == source.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.ArchiveFrom, r), ct);
            }
            if (fetched > 0)
                logger.LogInformation("Archiwum od {From}: {Sources} źródeł, {Added} wpisów, {Warnings} ostrzeżeń.", from, fetched, added, warnings.Count);
            return new ArchiveResult(fetched, added, warnings);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <returns>Dodane wpisy, do jakiego dnia lista jest pełna (null – nie dotarto) i ostrzeżenie.</returns>
    private async Task<(int Added, DateOnly? Reached, string? Warning)> WalkArchiveAsync(ZdrowieInfoDbContext db, Source source, DateOnly from, CancellationToken ct)
    {
        DateOnly? reached = null;
        var max = Math.Max(1, options.Value.MaxArchivePages);
        var found = new Dictionary<string, ExtractedItem>();
        var url = source.Url;
        var overflow = false;
        string? warning = null;
        for (var page = 1; ; page++)
        {
            var fetch = await fetcher.FetchAsync(url, ct: ct);
            if (!fetch.Success || fetch.Html is null)
            {
                warning = $"nie udało się pobrać {page}. strony listy ({fetch.Message}).";
                break;
            }
            ListExtraction extraction;
            try
            {
                extraction = SourceExtractor.ExtractList(fetch.Html, url, source.Selectors);
            }
            catch (SelectorException e)
            {
                warning = $"{page}. strona listy: {e.Message}";
                break;
            }
            var dated = extraction.Items.Where(i => i.PublishedOn is not null).ToList();
            if (dated.Count == 0)
            {
                warning = "wpisy na liście nie mają dat – nie da się sięgnąć wstecz.";
                break;
            }
            foreach (var item in dated)
                if (item.PublishedOn >= from) found.TryAdd(item.Key, item);
            if (dated.Min(i => i.PublishedOn!.Value) < from)
            {
                reached = from;
                break;
            }

            var next = extraction.NextPageUrl;
            if (next is null && !overflow && source.Selectors.OverflowUrl is { } overflowUrl)
            {
                next = overflowUrl;
                overflow = true;
            }
            if (next is null)
            {
                reached = DateOnly.MinValue; // koniec listy – starszych wpisów na stronie nie ma
                break;
            }
            if (page >= max)
            {
                warning = $"przejrzano {page} stron listy i nie dotarto do {from:dd.MM.yyyy} (limit MaxArchivePages).";
                break;
            }
            url = next;
        }

        var keys = found.Keys.ToList();
        var known = (await db.SourceItems.Where(i => i.SourceId == source.Id && keys.Contains(i.Key)).Select(i => i.Key).ToListAsync(ct)).ToHashSet();
        var now = time.GetUtcNow();
        var added = 0;
        foreach (var item in found.Values.Where(i => !known.Contains(i.Key)))
        {
            db.SourceItems.Add(new SourceItem
            {
                SourceId = source.Id,
                Kind = SourceItemKind.Entry,
                Key = item.Key,
                Url = item.Url,
                Title = item.Title,
                PublishedOn = item.PublishedOn,
                Summary = item.Summary,
                IsBaseline = true,
                FirstSeenAt = now,
                LastChangedAt = now,
            });
            added++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return (added, reached, warning);
    }

    /// <summary>Treść wskazanych wpisów (raport wstecz i okresowy) – także zastanych, których zwykłe sprawdzenie nie pobiera.</summary>
    public async Task FetchContentAsync(IReadOnlyCollection<long> itemIds, CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var sourceIds = await db.SourceItems.Where(i => itemIds.Contains(i.Id) && i.Kind == SourceItemKind.Entry && i.DetailFetchedAt == null && i.DetailAttempts < 3)
                .Select(i => i.SourceId).Distinct().ToListAsync(ct);
            var details = new ConcurrentDictionary<string, string?>();
            foreach (var source in await db.Sources.Where(s => sourceIds.Contains(s.Id)).ToListAsync(ct))
                await FetchDetailsAsync(db, source, details, ct, itemIds);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task FetchDetailsAsync(ZdrowieInfoDbContext db, Source source, ConcurrentDictionary<string, string?> details, CancellationToken ct,
        IReadOnlyCollection<long>? onlyIds = null)
    {
        var query = db.SourceItems.Where(i => i.SourceId == source.Id && i.Kind == SourceItemKind.Entry && i.DetailFetchedAt == null && i.DetailAttempts < 3);
        query = onlyIds is null ? query.Where(i => !i.IsBaseline) : query.Where(i => onlyIds.Contains(i.Id));
        var pending = await query
            .OrderBy(i => i.FirstSeenAt)
            .Take(onlyIds is null ? options.Value.MaxDetailFetchesPerSource : int.MaxValue)
            .ToListAsync(ct);

        foreach (var item in pending)
        {
            var now = time.GetUtcNow();
            if (!details.TryGetValue(item.Key, out var text))
            {
                // Ten sam artykuł mógł już zostać pobrany dla innego źródła (np. NFZ Aktualności i Komunikaty DI).
                text = await db.SourceItems.AsNoTracking()
                    .Where(i => i.Url == item.Url && i.Id != item.Id && i.Content != null && i.DetailFetchedAt != null && i.LastChangedAt >= item.LastChangedAt)
                    .Select(i => i.Content)
                    .FirstOrDefaultAsync(ct);
                if (text is null)
                {
                    var fetch = await fetcher.FetchAsync(item.Url, ct: ct);
                    if (!fetch.Success || fetch.Html is null)
                    {
                        item.DetailAttempts++;
                        item.DetailError = fetch.Message ?? "Brak treści.";
                        if (item.DetailAttempts >= 3) item.Content ??= item.Summary;
                        await db.SaveChangesAsync(ct);
                        continue;
                    }
                    try
                    {
                        text = SourceExtractor.ExtractDetailText(fetch.Html, item.Url, source.Selectors);
                    }
                    catch (SelectorException e)
                    {
                        item.DetailError = e.Message;
                    }
                }
                details[item.Key] = text;
            }

            item.Content = text ?? item.Summary;
            item.ContentHash = item.Content is null ? null : SourceExtractor.Hash(item.Content);
            item.DetailFetchedAt = now;
            item.DetailAttempts++;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task MarkSuccessAsync(ZdrowieInfoDbContext db, Source source, DateTimeOffset now, CancellationToken ct)
    {
        source.LastCheckedAt = now;
        source.LastSuccessAt = now;
        source.ConsecutiveFailures = 0;
        source.FailingSince = null;
        source.NextRetryAt = null;
        source.Status = SourceStatus.Ok;
        source.StatusMessage = null;

        var open = await db.SourceIncidents.Where(i => i.SourceId == source.Id && i.EndedAt == null).ToListAsync(ct);
        foreach (var incident in open)
        {
            incident.EndedAt = now;
            logger.LogInformation("Źródło {Slug}: dostęp przywrócony po {Duration}.", source.Slug, now - incident.StartedAt);
        }
    }

    private async Task MarkFailureAsync(ZdrowieInfoDbContext db, Source source, CheckErrorType error, string message, DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value;
        source.LastCheckedAt = now;
        source.ConsecutiveFailures++;
        source.FailingSince ??= now;
        source.StatusMessage = message;

        var open = await db.SourceIncidents.Where(i => i.SourceId == source.Id && i.EndedAt == null).FirstOrDefaultAsync(ct);
        if (open is not null)
        {
            // Incydent już otwarty: bez nowych powiadomień, tylko licznik prób; kolejna próba za godzinę.
            open.Attempts++;
            open.LastAttemptAt = now;
            open.ErrorType = error;
            open.Details = message;
            source.NextRetryAt = now + o.IncidentRecheckInterval;
            source.Status = SourceStatus.NeedsAttention;
        }
        else if (source.ConsecutiveFailures >= o.FailuresBeforeIncident)
        {
            db.SourceIncidents.Add(new SourceIncident
            {
                SourceId = source.Id,
                StartedAt = source.FailingSince.Value,
                ErrorType = error,
                Details = message,
                Attempts = source.ConsecutiveFailures,
                LastAttemptAt = now,
            });
            source.NextRetryAt = now + o.IncidentRecheckInterval;
            source.Status = SourceStatus.NeedsAttention;
            logger.LogWarning("Źródło {Slug}: incydent po {Attempts} próbach ({Error}).", source.Slug, source.ConsecutiveFailures, error);
        }
        else
        {
            // Pojedynczy błąd naprawiony ponowieniem nie jest incydentem: +5 min, potem +10 min.
            source.NextRetryAt = now + o.RetryDelay;
            source.Status = SourceStatus.Retrying;
        }
    }
}
