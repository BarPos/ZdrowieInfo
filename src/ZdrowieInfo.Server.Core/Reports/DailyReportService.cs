using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Export;
using ZdrowieInfo.Server.Push;
using ZdrowieInfo.Server.Sources;
using ZdrowieInfo.Server.Sources.Checking;

namespace ZdrowieInfo.Server.Reports;

public enum ReportRunOutcome
{
    Created,
    AlreadyExists,
    /// <summary>Błąd przejściowy (API) – ponowić za 10 minut.</summary>
    RetryLater,
    /// <summary>Zastępowanie się nie udało – poprzedni raport zostaje bez zmian.</summary>
    NotReplaced,
    /// <summary>Żądanie niemożliwe (np. data z przyszłości, za szeroki okres) – nic nie zmieniono.</summary>
    Rejected,
}

/// <param name="Notes">Uwagi dla administratora (np. źródła, których archiwum nie sięga tak daleko).</param>
public sealed record ReportRunResult(ReportRunOutcome Outcome, long? ReportId = null, string? Error = null, IReadOnlyList<string>? Notes = null);

public enum ExternalReportOutcome { Created, Conflict, Invalid }

public sealed record ExternalReportResult(ExternalReportOutcome Outcome, long? ReportId, ReportStatus Status, IReadOnlyList<string> Errors);

/// <summary>
/// Jeden raport dziennie: „Brak zmian” bez wywołania API; z Claude, gdy są nowe wpisy (walidacja, jedna poprawka,
/// potem „do weryfikacji”); awaryjny z listą wpisów, gdy analiza się nie uda.
/// </summary>
public sealed class DailyReportService(
    IDbContextFactory<ZdrowieInfoDbContext> dbFactory,
    IReportGenerator generator,
    ReportSettings settings,
    TimeProvider time,
    ReportExporter exporter,
    IOptions<ExportOptions> exportOptions,
    NotificationDispatcher notifications,
    SourceCheckService sources,
    ILogger<DailyReportService> logger)
{
    /// <summary>Najdłuższy okres raportu okresowego.</summary>
    public const int MaxPeriodDays = 92;

    /// <summary>Najwięcej wpisów w jednym raporcie wstecz/okresowym – każdy trzeba rozliczyć w jednej odpowiedzi modelu.</summary>
    public const int MaxRetrospectiveInputs = 150;

    private DateOnly Today => DateOnly.FromDateTime(WarsawClock.ToLocal(time.GetUtcNow()));

    /// <summary>
    /// Dokument .tex i PDF po zapisaniu raportu. Błąd eksportu nie wpływa na raport ani powiadomienie – trafia do logu
    /// i pola PdfError, a PDF można wygenerować ponownie przy pobraniu.
    /// </summary>
    private Task ExportQuietlyAsync(DateOnly date) => ExportQuietlyAsync(ReportKey.Daily(date));

    private async Task ExportQuietlyAsync(ReportKey key)
    {
        if (!exportOptions.Value.Enabled) return;
        try
        {
            var result = await exporter.ExportAsync(key);
            if (!result.Success) logger.LogWarning("Raport {Key}: PDF niewygenerowany – {Error}", key, result.Error);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Raport {Key}: eksport PDF przerwany.", key);
        }
    }

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="replace">Zastępuje istniejący raport (wpisy z niego wracają do wejścia).</param>
    /// <param name="finalAttempt">Przy błędzie przejściowym nie czekać – od razu raport awaryjny.</param>
    public async Task<ReportRunResult> GenerateAsync(DateOnly date, bool replace = false, bool finalAttempt = true, CancellationToken ct = default)
    {
        var result = await GenerateCoreAsync(date, replace, finalAttempt, ct);
        if (result.Outcome == ReportRunOutcome.Created)
        {
            await notifications.DispatchPendingAsync(ct); // push od razu, nie czeka na LaTeX
            await ExportQuietlyAsync(date);
        }
        return result;
    }

    private async Task<ReportRunResult> GenerateCoreAsync(DateOnly date, bool replace, bool finalAttempt, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.Reports.AsNoTracking().WhereKey(ReportKey.Daily(date)).SingleOrDefaultAsync(ct);
            if (existing is not null && !replace) return new ReportRunResult(ReportRunOutcome.AlreadyExists, existing.Id);
            var replaceId = existing?.Id;

            // Przy zastępowaniu wejście obejmuje też wpisy z obecnego raportu; stary raport znika dopiero razem z zapisem nowego.
            // Raport z przeszłości zastępowany jest tylko ze swoich wpisów – nowe wpisy czekają na najbliższy raport dzienny.
            var now = time.GetUtcNow();
            var withPending = replaceId is null || date >= Today;
            var pending = await (from i in db.SourceItems.AsNoTracking()
                                 join s in db.Sources on i.SourceId equals s.Id
                                 where ((withPending && i.ReportedAt == null) || (replaceId != null && i.ReportId == replaceId)) && !i.IsBaseline
                                 select new PendingItem(i, s.Name)).ToListAsync(ct);
            var previous = await db.Reports.AsNoTracking().Daily().Where(r => r.Date < date).OrderByDescending(r => r.Date).Select(r => (DateTimeOffset?)r.CreatedAt).FirstOrDefaultAsync(ct);
            var sourcesState = await SourcesStateBuilder.BuildAsync(db, previous ?? now.AddDays(-1), ct);

            if (pending.Count == 0)
            {
                var empty = new Report
                {
                    Date = date,
                    IsEmpty = true,
                    Status = ReportStatus.Ready,
                    Title = $"Brak zmian – {date:dd.MM.yyyy}",
                    Summary = "W monitorowanych źródłach nie pojawiły się nowe wpisy.",
                    Producer = "system",
                    SourcesState = sourcesState,
                    PushSent = date < Today, // ponowne przygotowanie raportu z przeszłości to nie nowość dnia
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                await SaveAsync(db, empty, [], now, ct, replaceId);
                logger.LogInformation("Raport {Date}: brak zmian.", date);
                return new ReportRunResult(ReportRunOutcome.Created, empty.Id);
            }

            var inputs = ReportComposer.BuildInputs(pending);
            var profile = await settings.GetProfileAsync(ct);
            var attempts = await settings.GetAttemptsAsync(ct);
            var attemptNo = (attempts?.Date == date ? attempts.Attempts : 0) + 1;

            Report report;
            try
            {
                report = await AnalyzeAsync(new GenerationRequest(date, profile, inputs), ct);
            }
            catch (ReportGenerationException e) when (e.Transient && !finalAttempt)
            {
                await settings.SetAttemptsAsync(new ReportAttempts(date, attemptNo, now, e.Message), ct);
                logger.LogWarning("Raport {Date}: próba {Attempt} nieudana ({Error}) – ponowienie później.", date, attemptNo, e.Message);
                return new ReportRunResult(ReportRunOutcome.RetryLater, Error: e.Message);
            }
            catch (ReportGenerationException e) when (replaceId is not null)
            {
                logger.LogError("Raport {Date}: ponowna analiza nieudana ({Error}) – poprzedni raport zostaje.", date, e.Message);
                return new ReportRunResult(ReportRunOutcome.NotReplaced, replaceId, e.Message);
            }
            catch (ReportGenerationException e)
            {
                logger.LogError("Raport {Date}: analiza nieudana ({Error}) – raport awaryjny z listą wpisów.", date, e.Message);
                report = new Report
                {
                    Date = date,
                    Status = ReportStatus.AiError,
                    Title = $"Wykryte zmiany bez analizy – {date:dd.MM.yyyy}",
                    Summary = $"Nie udało się przygotować analizy ({e.Message}). Poniżej {inputs.Count} wykrytych wpisów do samodzielnego przejrzenia.",
                    Producer = "system",
                    Error = e.Message,
                };
            }

            report.Date = date;
            report.GenerationAttempts = attemptNo;
            report.SourcesState = sourcesState;
            report.PushSent = date < Today; // ponowne przygotowanie raportu z przeszłości to nie nowość dnia
            report.CreatedAt = now;
            report.UpdatedAt = now;
            report.InputItemIds = inputs.SelectMany(i => i.SourceItemIds).Distinct().Order().ToList();
            await SaveAsync(db, report, report.InputItemIds, now, ct, replaceId);
            logger.LogInformation("Raport {Date}: {Status}, {Findings} punktów, {Items} wpisów.", date, report.Status, report.Findings.Count, inputs.Count);
            return new ReportRunResult(ReportRunOutcome.Created, report.Id);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Raport na żądanie administratora (panel, wiersz poleceń): dzienny za dowolny dzień do dziś albo okresowy od–do.
    /// Dziś i ponowne przygotowanie raportu z harmonogramu – jak <see cref="GenerateAsync"/> (wpisy, które raport „zabrał”).
    /// Pozostałe – raport wstecz z wpisów opublikowanych w tych dniach; brakujące wpisy i ich treść pobierane są ze stron.
    /// Bez powiadomień push (to nie jest nowy raport dnia).
    /// </summary>
    public async Task<ReportRunResult> GenerateForAsync(DateOnly from, DateOnly to, bool replace = false, CancellationToken ct = default)
    {
        var today = Today;
        if (from > to) return new ReportRunResult(ReportRunOutcome.Rejected, Error: "Data początkowa jest późniejsza niż końcowa.");
        if (to > today) return new ReportRunResult(ReportRunOutcome.Rejected, Error: "Nie można przygotować raportu za dni, które jeszcze nie minęły.");
        if (to.DayNumber - from.DayNumber + 1 > MaxPeriodDays)
            return new ReportRunResult(ReportRunOutcome.Rejected, Error: $"Okres może mieć najwyżej {MaxPeriodDays} dni.");

        var key = from == to ? ReportKey.Daily(to) : ReportKey.Period(from, to);
        if (key.Kind == ReportKind.Daily)
        {
            if (to == today) return await GenerateAsync(to, replace, finalAttempt: true, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.Reports.AsNoTracking().WhereKey(key).Select(r => (long?)r.Id).SingleOrDefaultAsync(ct);
            if (existing is { } id && replace && await db.SourceItems.AnyAsync(i => i.ReportId == id, ct))
                return await GenerateAsync(to, replace: true, finalAttempt: true, ct);
        }

        var result = await GenerateRetrospectiveAsync(key, replace, ct);
        if (result.Outcome == ReportRunOutcome.Created) await ExportQuietlyAsync(key);
        return result;
    }

    private async Task<ReportRunResult> GenerateRetrospectiveAsync(ReportKey key, bool replace, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            long? replaceId;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var existing = await db.Reports.AsNoTracking().WhereKey(key).Select(r => (long?)r.Id).SingleOrDefaultAsync(ct);
                if (existing is not null && !replace) return new ReportRunResult(ReportRunOutcome.AlreadyExists, existing);
                replaceId = existing;
            }

            // Wpisy z tych dni: najpierw archiwum list (źródła, których znane wpisy nie sięgają tak daleko), potem treść wpisów.
            var archive = await sources.EnsureArchiveAsync(key.Start, ct);
            var notes = archive.Warnings.ToList();
            var ids = await SelectRetrospectiveAsync(key, ct);
            await using var db2 = await dbFactory.CreateDbContextAsync(ct);
            var query = from i in db2.SourceItems.AsNoTracking()
                        join s in db2.Sources on i.SourceId equals s.Id
                        where ids.Contains(i.Id)
                        select new PendingItem(i, s.Name);
            // Liczba wpisów przed pobieraniem treści – za szeroki okres nie pobiera setek artykułów, żeby potem odmówić.
            var count = ReportComposer.BuildInputs(await query.ToListAsync(ct)).Count;
            if (count > MaxRetrospectiveInputs)
                return new ReportRunResult(ReportRunOutcome.Rejected,
                    Error: $"W tym okresie jest {count} wpisów – w jednym raporcie najwyżej {MaxRetrospectiveInputs}. Wybierz krótszy okres.", Notes: notes);
            if (ids.Count > 0) await sources.FetchContentAsync(ids, ct);
            var inputs = ReportComposer.BuildInputs(await query.ToListAsync(ct));

            var now = time.GetUtcNow();
            Report report;
            if (inputs.Count == 0)
            {
                report = new Report
                {
                    IsEmpty = true,
                    Status = ReportStatus.Ready,
                    Title = key.Kind == ReportKind.Daily ? $"Brak wpisów – {key.Label}" : $"Brak wpisów – {key.Label}",
                    Summary = key.Kind == ReportKind.Daily
                        ? "W monitorowanych źródłach nie ma wpisów opublikowanych tego dnia."
                        : "W monitorowanych źródłach nie ma wpisów opublikowanych w tym okresie.",
                    Producer = "system",
                };
            }
            else
            {
                var profile = await settings.GetProfileAsync(ct);
                var request = new GenerationRequest(key.End, profile, inputs, PeriodStart: key.Kind == ReportKind.Period ? key.Start : null, Retrospective: true);
                try
                {
                    report = await AnalyzeAsync(request, ct);
                }
                catch (ReportGenerationException e) when (replaceId is not null)
                {
                    logger.LogError("Raport {Key}: ponowna analiza nieudana ({Error}) – poprzedni raport zostaje.", key, e.Message);
                    return new ReportRunResult(ReportRunOutcome.NotReplaced, replaceId, e.Message, notes);
                }
                catch (ReportGenerationException e)
                {
                    logger.LogError("Raport {Key}: analiza nieudana ({Error}) – raport awaryjny z listą wpisów.", key, e.Message);
                    report = new Report
                    {
                        Status = ReportStatus.AiError,
                        Title = $"Wpisy bez analizy – {key.Label}",
                        Summary = $"Nie udało się przygotować analizy ({e.Message}). Poniżej {inputs.Count} wpisów do samodzielnego przejrzenia.",
                        Producer = "system",
                        Error = e.Message,
                    };
                }
            }

            report.Kind = key.Kind;
            report.PeriodStart = key.Start;
            report.Date = key.End;
            report.GenerationAttempts = 1;
            report.SourcesState = [];
            report.InputItemIds = ids.Order().ToList();
            report.PushSent = true; // raport wstecz to nie nowość dnia – bez powiadomienia
            if (notes.Count > 0) report.ValidationIssues = [.. report.ValidationIssues, .. notes.Select(n => "Archiwum: " + n)];
            report.CreatedAt = now;
            report.UpdatedAt = now;
            await SaveAsync(db2, report, [], now, ct, replaceId);
            logger.LogInformation("Raport {Key} (wstecz): {Status}, {Findings} punktów, {Items} wpisów.", key, report.Status, report.Findings.Count, inputs.Count);
            return new ReportRunResult(ReportRunOutcome.Created, report.Id, Notes: notes);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Wpisy opublikowane w dniach raportu (bez daty publikacji – wykryte w tych dniach).</summary>
    private async Task<List<long>> SelectRetrospectiveAsync(ReportKey key, CancellationToken ct)
    {
        var fromUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(key.Start.ToDateTime(TimeOnly.MinValue), WarsawClock.Zone));
        var toUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(key.End.AddDays(1).ToDateTime(TimeOnly.MinValue), WarsawClock.Zone));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SourceItems
            .Where(i => (i.PublishedOn != null && i.PublishedOn >= key.Start && i.PublishedOn <= key.End)
                        || (i.PublishedOn == null && !i.IsBaseline && i.FirstSeenAt >= fromUtc && i.FirstSeenAt < toUtc))
            .Select(i => i.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Usuwa raport z plikami PDF/.tex i publicznym linkiem. Wpisy zostają zaraportowane (nie wracają do najbliższego
    /// raportu dziennego). Dzisiejszego raportu harmonogram już nie odtworzy – można go przygotować ręcznie.
    /// </summary>
    public async Task<bool> DeleteAsync(ReportKey key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var report = await db.Reports.AsNoTracking().WhereKey(key).Select(r => new { r.Id, r.PdfPath, r.TexPath }).SingleOrDefaultAsync(ct);
            if (report is null) return false;
            await db.Reports.Where(r => r.Id == report.Id).ExecuteDeleteAsync(ct); // source_items.report_id → null, reported_at zostaje
            foreach (var path in new[] { report.PdfPath, report.TexPath })
                if (path is not null && File.Exists(path)) File.Delete(path);
            if (key.Kind == ReportKind.Daily && key.End == Today) await settings.SetSkipDateAsync(key.End, ct);
            logger.LogInformation("Raport {Key} usunięty.", key);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Publiczny link do PDF: istniejący albo nowy (<paramref name="renew"/> – nowy losowy, stary przestaje działać).</summary>
    public async Task<string?> ShareAsync(ReportKey key, bool renew = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var report = await db.Reports.WhereKey(key).SingleOrDefaultAsync(ct);
        if (report is null) return null;
        if (report.ShareToken is null || renew)
        {
            report.ShareToken = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            report.SharedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Raport {Key}: {Action} publiczny link.", key, renew ? "nowy" : "utworzony");
        }
        return report.ShareToken;
    }

    public async Task UnshareAsync(ReportKey key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Reports.WhereKey(key).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.ShareToken, (string?)null)
            .SetProperty(r => r.SharedAt, (DateTimeOffset?)null), ct);
        logger.LogInformation("Raport {Key}: publiczny link wycofany.", key);
    }

    /// <summary>
    /// Raport od zewnętrznego producenta (POST /api/reports). Cytaty z identyfikatorem wpisu muszą być dosłowne (inaczej 422);
    /// cytaty z samym URL-em spoza wejścia są zapisywane jako niezweryfikowane, a raport dostaje status „do weryfikacji”.
    /// Za zaraportowane uznawane są tylko wpisy użyte w cytatach albo na liście pominiętych.
    /// </summary>
    public async Task<ExternalReportResult> SaveExternalAsync(DateOnly date, ReportDocument document, string producer, bool replace, CancellationToken ct = default)
    {
        var result = await SaveExternalCoreAsync(date, document, producer, replace, ct);
        if (result.Outcome == ExternalReportOutcome.Created)
        {
            await notifications.DispatchPendingAsync(ct);
            await ExportQuietlyAsync(date);
        }
        return result;
    }

    private async Task<ExternalReportResult> SaveExternalCoreAsync(DateOnly date, ReportDocument document, string producer, bool replace, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.Reports.AsNoTracking().WhereKey(ReportKey.Daily(date)).SingleOrDefaultAsync(ct);
            if (existing is not null && !replace) return new ExternalReportResult(ExternalReportOutcome.Conflict, existing.Id, existing.Status, []);

            var pending = await (from i in db.SourceItems.AsNoTracking()
                                 join s in db.Sources on i.SourceId equals s.Id
                                 where (i.ReportedAt == null || (existing != null && i.ReportId == existing.Id)) && !i.IsBaseline
                                 select new PendingItem(i, s.Name)).ToListAsync(ct);
            var inputs = ReportComposer.BuildInputs(pending);
            var validation = ReportValidator.Validate(document, inputs, requireCoverage: false, allowUrlCitations: true);
            // Niepoprawny raport niczego nie zmienia – także przy replace=true.
            if (!validation.IsValid) return new ExternalReportResult(ExternalReportOutcome.Invalid, null, ReportStatus.NeedsReview, validation.Errors);

            var now = time.GetUtcNow();
            var report = ToReport(document, validation, inputs);
            var unverified = validation.Citations.Count(c => !c.Verified);
            report.Date = date;
            report.Status = unverified > 0 ? ReportStatus.NeedsReview : ReportStatus.Ready;
            report.Producer = producer.Length > 100 ? producer[..100] : producer;
            report.ValidationIssues = unverified > 0 ? [$"{unverified} cytatów spoza monitorowanych wpisów – niezweryfikowane."] : [];
            var previous = await db.Reports.AsNoTracking().Daily().Where(r => r.Date < date).OrderByDescending(r => r.Date).Select(r => (DateTimeOffset?)r.CreatedAt).FirstOrDefaultAsync(ct);
            report.SourcesState = await SourcesStateBuilder.BuildAsync(db, previous ?? now.AddDays(-1), ct);
            report.CreatedAt = now;
            report.UpdatedAt = now;

            var covered = validation.Citations.Where(c => c.Item is not null).SelectMany(c => c.Item!.SourceItemIds)
                .Concat(inputs.Where(i => document.Ignored.Any(g => g.ItemId == i.Id)).SelectMany(i => i.SourceItemIds));
            report.InputItemIds = covered.Distinct().Order().ToList();
            await SaveAsync(db, report, report.InputItemIds, now, ct, existing?.Id);
            logger.LogInformation("Raport {Date} od {Producer}: {Status}, {Findings} punktów.", date, producer, report.Status, report.Findings.Count);
            return new ExternalReportResult(ExternalReportOutcome.Created, report.Id, report.Status, report.ValidationIssues);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Analiza przez generator: walidacja, jedna poprawka z listą błędów, potem „do weryfikacji”.</summary>
    private async Task<Report> AnalyzeAsync(GenerationRequest request, CancellationToken ct)
    {
        var (date, inputs) = (request.Key, request.Items);
        var first = await generator.GenerateAsync(request, ct);
        var usage = first.Usage;
        var result = first;
        var validation = ReportValidator.Validate(first.Document, inputs, requireCoverage: true);

        if (!validation.IsValid)
        {
            logger.LogWarning("Raport {Date}: odpowiedź nie przeszła kontroli ({Count} uwag) – ponowienie z listą błędów.", date, validation.Errors.Count);
            var second = await generator.GenerateAsync(request with { PreviousErrors = validation.Errors }, ct);
            usage = Sum(usage, second.Usage);
            var secondValidation = ReportValidator.Validate(second.Document, inputs, requireCoverage: true);
            // Zostaje lepsza z dwóch odpowiedzi (mniej uwag).
            if (secondValidation.Errors.Count <= validation.Errors.Count)
            {
                result = second;
                validation = secondValidation;
            }
        }

        // Pusta albo ucięta odpowiedź (np. limit tokenów przy wielu wpisach) to nie raport do weryfikacji – raport awaryjny z listą wpisów.
        if (string.IsNullOrWhiteSpace(result.Document.Title) && result.Document.Findings.Count == 0 && result.Document.Ignored.Count == 0)
            throw new ReportGenerationException("odpowiedź modelu była pusta albo ucięta (dwukrotnie)", transient: false);

        var report = ToReport(result.Document, validation, inputs);
        report.Status = validation.IsValid ? ReportStatus.Ready : ReportStatus.NeedsReview;
        report.ValidationIssues = [.. validation.Errors];
        report.Producer = generator.Name;
        report.Model = result.Model;
        report.AiUsage = usage;
        return report;
    }

    /// <summary>Zamienia dokument na encje; cytaty dostają kopię tytułu, adresu i daty wpisu.</summary>
    public static Report ToReport(ReportDocument document, ReportValidation validation, IReadOnlyList<ReportInputItem> inputs)
    {
        var report = new Report
        {
            Title = document.Title.Trim(),
            Summary = document.Summary.Trim(),
            Producer = "",
            Body = document,
        };
        var order = 0;
        foreach (var (finding, index) in document.Findings.Select((f, i) => (f, i))
                     .OrderBy(x => ReportVocabulary.Importances.GetValueOrDefault(x.f.Importance, Importance.Low))
                     .ThenBy(x => x.i))
        {
            var entity = new ReportFinding
            {
                Order = order++,
                Importance = ReportVocabulary.Importances.GetValueOrDefault(finding.Importance, Importance.Low),
                Category = ReportVocabulary.Categories.GetValueOrDefault(finding.Category, FindingCategory.Other),
                Headline = finding.Headline.Trim(),
                Body = finding.Body.Trim(),
                Action = string.IsNullOrWhiteSpace(finding.Action) ? null : finding.Action.Trim(),
            };
            for (var c = 0; c < finding.Citations.Count; c++)
            {
                var citation = finding.Citations[c];
                var check = validation.Citations.FirstOrDefault(x => x.Finding == index && x.Citation == c);
                var item = check?.Item;
                entity.Citations.Add(new FindingCitation
                {
                    SourceItemId = item?.SourceItemIds[0],
                    Title = item?.Title ?? citation.Url ?? citation.ItemId ?? "",
                    Url = item?.Url ?? citation.Url ?? "",
                    PublishedOn = item?.PublishedOn,
                    Quote = citation.Quote.Trim(),
                    Verified = check?.Verified ?? false,
                });
            }
            report.Findings.Add(entity);
        }
        return report;
    }

    /// <summary>
    /// Zapis raportu i oznaczenie wpisów jako zaraportowanych – w jednej transakcji. Przy zastępowaniu stary raport jest
    /// usuwany (a jego wpisy zwolnione) dopiero tutaj, razem z zapisem nowego.
    /// </summary>
    public static async Task SaveAsync(ZdrowieInfoDbContext db, Report report, IEnumerable<long> sourceItemIds, DateTimeOffset now, CancellationToken ct, long? replaceId = null)
    {
        var ids = sourceItemIds.Distinct().ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (replaceId is not null)
        {
            // Publiczny link przeżywa ponowne przygotowanie raportu (znika dopiero z usunięciem albo wycofaniem).
            var share = await db.Reports.Where(r => r.Id == replaceId).Select(r => new { r.ShareToken, r.SharedAt }).SingleAsync(ct);
            report.ShareToken ??= share.ShareToken;
            report.SharedAt ??= share.SharedAt;
            await db.SourceItems.Where(i => i.ReportId == replaceId)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.ReportId, (long?)null).SetProperty(i => i.ReportedAt, (DateTimeOffset?)null), ct);
            await db.Reports.Where(r => r.Id == replaceId).ExecuteDeleteAsync(ct);
        }
        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);
        await db.SourceItems.Where(i => ids.Contains(i.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ReportId, report.Id).SetProperty(i => i.ReportedAt, now), ct);
        await tx.CommitAsync(ct);
    }

    private static AiUsage Sum(AiUsage a, AiUsage b) =>
        new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.CacheReadTokens + b.CacheReadTokens, a.CacheWriteTokens + b.CacheWriteTokens, a.Calls + b.Calls);
}
