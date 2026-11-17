using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Settings;
using ZdrowieInfo.Server.Sources.Checking;

namespace ZdrowieInfo.Server.Updates;

public enum ServerUpdateState { Downloading, Staged, Succeeded, RolledBack, Failed }

/// <summary>Jedna aktualizacja serwera (tabela <c>update_runs</c>): z panelu albo z install.sh --upgrade.</summary>
public sealed class ServerUpdateRun
{
    public Guid Id { get; set; }
    public required string FromVersion { get; set; }
    public required string ToVersion { get; set; }
    public required string RequestedBy { get; set; }
    public ServerUpdateState State { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed record UpdateCheck(DateTimeOffset CheckedAt, ReleaseInfo? Latest, string? Error);

public sealed record ServerUpdateStatus(
    bool Configured,
    string CurrentVersion,
    ReleaseInfo? Latest,
    bool UpdateAvailable,
    DateTimeOffset? LastCheckedAt,
    string? LastCheckError,
    IReadOnlyList<ServerUpdateRun> Runs,
    bool SelfUpdateSupported,
    string? UnsupportedReason);

public sealed record UpdateResult(bool Ok, string? Error = null)
{
    public static readonly UpdateResult Success = new(true);
    public static UpdateResult Fail(string error) => new(false, error);
}

/// <summary>
/// Aktualizacja serwera z panelu: sprawdzanie wydań, pobranie paczki do <c>&lt;DataDirectory&gt;/updates/&lt;wersja&gt;/</c>
/// (z kontrolą SHA-256), zlecenie <c>request.conf</c> dla update.sh (uruchamianego jako root przez
/// zdrowieinfo-update.path) i odczyt wyniku <c>result.conf</c>, który zostawia update.sh. Serwer nigdy nie potrzebuje roota.
/// </summary>
public sealed class ServerUpdateService(
    IReleaseSource releases,
    IServerInstallation installation,
    AppSettingsStore settings,
    IDbContextFactory<ZdrowieInfoDbContext> dbFactory,
    IOptions<ZdrowieInfoOptions> app,
    SourceCheckService sourceChecks,
    ReportJobQueue reportJobs,
    DailyReportService reportService,
    TimeProvider time,
    ILogger<ServerUpdateService> logger)
{
    public const long MaxTarballBytes = 1024L * 1024 * 1024;
    private const string LastCheckKey = "updates.last_check";

    /// <summary>Zlecenie pobierane/oczekujące dłużej niż godzinę nie blokuje nowego.</summary>
    public static readonly TimeSpan StaleRunAfter = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    public string UpdatesDirectory => Path.Combine(app.Value.DataDirectory, "updates");

    public async Task<ServerUpdateStatus> GetStatusAsync(CancellationToken ct = default)
    {
        // Wynik zapisany po starcie tego procesu (update.sh pisze go, gdy nowa wersja już działa).
        await ProcessResultFileAsync(ct);
        var last = await settings.GetAsync<UpdateCheck>(LastCheckKey, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var runs = await db.UpdateRuns.AsNoTracking().OrderByDescending(r => r.RequestedAt).Take(10).ToListAsync(ct);
        var configured = releases.IsConfigured;
        var latest = configured ? last?.Latest : null;
        return new ServerUpdateStatus(configured, AppVersion.Current, latest, configured && CheckTargetVersion(latest?.Version) is null,
            last?.CheckedAt, configured ? last?.Error : null, runs, installation.SelfUpdateSupported, installation.UnsupportedReason);
    }

    public async Task<ServerUpdateStatus> CheckNowAsync(CancellationToken ct = default)
    {
        if (!releases.IsConfigured) return await GetStatusAsync(ct);
        await _checkLock.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            UpdateCheck record;
            try
            {
                var latest = await releases.GetLatestAsync(ct);
                record = new UpdateCheck(now, latest?.Info, latest is null ? "Nie znaleziono żadnego wydania." : null);
            }
            catch (Exception ex) when (ex is ReleaseSourceException or HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning(ex, "Sprawdzanie wydań nieudane.");
                record = new UpdateCheck(now, (await settings.GetAsync<UpdateCheck>(LastCheckKey, ct))?.Latest, ex.Message);
            }
            await settings.SetAsync(LastCheckKey, record, ct: ct);
        }
        finally
        {
            _checkLock.Release();
        }
        return await GetStatusAsync(ct);
    }

    public async Task<bool> IsCheckDueAsync(int hours, CancellationToken ct = default)
    {
        var last = await settings.GetAsync<UpdateCheck>(LastCheckKey, ct);
        return last is null || time.GetUtcNow() - last.CheckedAt >= TimeSpan.FromHours(hours <= 0 ? 24 : hours);
    }

    /// <summary>Pobiera najnowsze wydanie, sprawdza SHA-256 i zostawia zlecenie dla update.sh.</summary>
    public async Task<UpdateResult> UpdateNowAsync(string actor, CancellationToken ct = default)
    {
        if (!releases.IsConfigured) return UpdateResult.Fail("Aktualizacje nie są skonfigurowane (ZdrowieInfo:Updates:GitHubRepository).");
        if (!installation.SelfUpdateSupported)
            return UpdateResult.Fail(installation.UnsupportedReason ?? "Aktualizacja z panelu nie jest dostępna na tym serwerze.");
        var arch = installation.Architecture;
        if (arch is null) return UpdateResult.Fail("Nieobsługiwana architektura procesora.");
        // Kolejka raportów jest w pamięci, a sprawdzanie źródeł trzyma stan w trakcie – restart w tym momencie by je przerwał.
        if (sourceChecks.IsBusy) return UpdateResult.Fail("Trwa sprawdzanie źródeł – spróbuj za kilka minut.");
        if (reportJobs.HasPending || reportService.IsBusy) return UpdateResult.Fail("Trwa przygotowywanie raportu – spróbuj, gdy się skończy.");

        if (!await _updateLock.WaitAsync(0, ct)) return UpdateResult.Fail("Aktualizacja jest już przygotowywana.");
        Guid? runId = null;
        string? versionDir = null;
        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var cutoff = time.GetUtcNow() - StaleRunAfter;
                if (await db.UpdateRuns.AnyAsync(r => (r.State == ServerUpdateState.Downloading || r.State == ServerUpdateState.Staged) && r.RequestedAt > cutoff, ct))
                    return UpdateResult.Fail("Aktualizacja już trwa.");
            }

            Release? release;
            try
            {
                release = await releases.GetLatestAsync(ct);
            }
            catch (ReleaseSourceException ex)
            {
                return UpdateResult.Fail(ex.Message);
            }
            if (release is null) return UpdateResult.Fail("Nie znaleziono żadnego wydania.");
            var version = release.Info.Version;
            if (CheckTargetVersion(version) is { } refusal) return UpdateResult.Fail(refusal);

            var tarName = ReleaseAssetNames.ServerTarball(version, arch);
            var tarAsset = release.FindAsset(tarName);
            var sumsAsset = release.FindAsset(ReleaseAssetNames.Sums);
            if (tarAsset is null || sumsAsset is null)
                return UpdateResult.Fail($"Wydanie {release.Info.TagName} nie ma pliku {tarName} albo {ReleaseAssetNames.Sums}.");

            var run = new ServerUpdateRun
            {
                Id = Guid.NewGuid(), FromVersion = AppVersion.Current, ToVersion = version, RequestedBy = actor,
                State = ServerUpdateState.Downloading, RequestedAt = time.GetUtcNow(),
            };
            runId = run.Id;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                db.UpdateRuns.Add(run);
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation("Aktualizacja {From} → {To} zlecona przez {Actor}.", AppVersion.Current, version, actor);

            // Najpierw SHA256SUMS.txt, potem paczka (z liczeniem skrótu w trakcie zapisu).
            string sumsText;
            await using (var s = await releases.OpenAssetAsync(sumsAsset, ct))
                sumsText = await FileHashing.ReadSmallTextAsync(s, Sha256Sums.MaxBytes, ct);
            if (!Sha256Sums.Parse(sumsText).TryGetValue(tarName, out var expected))
                return await FailRunAsync(run.Id, $"{ReleaseAssetNames.Sums} nie ma wpisu dla {tarName}.", ct);

            Directory.CreateDirectory(UpdatesDirectory);
            versionDir = Path.Combine(UpdatesDirectory, version);
            FileHashing.TryDeleteDirectory(versionDir);
            Directory.CreateDirectory(versionDir);
            var partial = Path.Combine(versionDir, tarName + ".partial");
            (long Size, string Sha256, byte[] Head) copied;
            await using (var s = await releases.OpenAssetAsync(tarAsset, ct))
                copied = await FileHashing.CopyAndHashAsync(s, partial, MaxTarballBytes, ct);
            if (!string.Equals(copied.Sha256, expected, StringComparison.Ordinal))
            {
                FileHashing.TryDeleteDirectory(versionDir);
                return await FailRunAsync(run.Id, $"{tarName}: niezgodny SHA-256 (oczekiwano {expected}, jest {copied.Sha256}).", ct);
            }
            File.Move(partial, Path.Combine(versionDir, tarName), overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(versionDir, ReleaseAssetNames.Sums), sumsText, ct);

            // Zlecenie atomowo: jednostka path reaguje, gdy tylko request.conf istnieje.
            var request = UpdateRequestFile.BuildRequest(run.Id, version, AppVersion.Current, arch, tarName, copied.Sha256);
            var temp = Path.Combine(UpdatesDirectory, $".{UpdateRequestFile.RequestFileName}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temp, request, new UTF8Encoding(false), ct);
            File.Move(temp, Path.Combine(UpdatesDirectory, UpdateRequestFile.RequestFileName), overwrite: true);

            await SetRunStateAsync(run.Id, ServerUpdateState.Staged, $"Pobrano {tarName}; czeka na update.sh.", finished: false, ct);
            return UpdateResult.Success;
        }
        catch (Exception ex) when (ex is ReleaseSourceException or HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Aktualizacja nieudana.");
            if (versionDir is not null) FileHashing.TryDeleteDirectory(versionDir);
            if (runId is { } id) return await FailRunAsync(id, ex.Message, CancellationToken.None);
            return UpdateResult.Fail(ex.Message);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    /// <summary>Null, gdy wersja nadaje się na aktualizację; inaczej powód.</summary>
    public static string? CheckTargetVersion(string? version)
    {
        if (!AppVersion.IsPlainTriplet(version)) return $"Wersja wydania „{version}” nie ma postaci x.y.z.";
        if (AppVersion.Compare(version!, AppVersion.Current) <= 0)
            return $"Wydanie {version} nie jest nowsze niż działająca wersja {AppVersion.Current}.";
        return null;
    }

    /// <summary>
    /// Czyta <c>result.conf</c> od update.sh i zamyka pasujące zlecenie (albo zapisuje nowe, np. z install.sh --upgrade).
    /// Bez pliku: otwarte zlecenie na działającą wersję = udane; „pobieranie” z poprzedniego procesu = przerwane.
    /// </summary>
    public async Task ProcessResultFileAsync(CancellationToken ct = default)
    {
        var resultPath = Path.Combine(UpdatesDirectory, UpdateRequestFile.ResultFileName);
        var now = time.GetUtcNow();
        Dictionary<string, string>? result = null;
        try
        {
            var fi = new FileInfo(resultPath);
            if (fi.Exists && fi.LinkTarget is null && fi.Length < 64 * 1024)
                result = UpdateRequestFile.ParseKeyValues(await File.ReadAllTextAsync(resultPath, ct));
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Nie można odczytać {Path}.", resultPath);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (result is not null)
        {
            var state = result.GetValueOrDefault("state") switch
            {
                "Succeeded" => ServerUpdateState.Succeeded,
                "RolledBack" => ServerUpdateState.RolledBack,
                _ => ServerUpdateState.Failed,
            };
            var finished = now;
            if (result.GetValueOrDefault("finished_at") is { } f &&
                DateTimeOffset.TryParse(f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                finished = dt.ToUniversalTime();

            ServerUpdateRun? run = null;
            if (Guid.TryParse(result.GetValueOrDefault("run_id"), out var id))
                run = await db.UpdateRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null)
            {
                run = new ServerUpdateRun
                {
                    Id = id == Guid.Empty ? Guid.NewGuid() : id,
                    FromVersion = Truncate(result.GetValueOrDefault("from_version"), 32) ?? "",
                    ToVersion = Truncate(result.GetValueOrDefault("version"), 32) ?? "",
                    RequestedBy = "install.sh",
                    RequestedAt = finished,
                };
                db.UpdateRuns.Add(run);
            }
            run.State = state;
            run.Message = Truncate(result.GetValueOrDefault("message"), 2000);
            run.FinishedAt = finished;
            await db.SaveChangesAsync(ct);
            FileHashing.TryDelete(resultPath);
            if (AppVersion.IsPlainTriplet(run.ToVersion)) FileHashing.TryDeleteDirectory(Path.Combine(UpdatesDirectory, run.ToVersion));
            logger.LogInformation("Aktualizacja {From} → {To}: {State}. {Message}", run.FromVersion, run.ToVersion, state, run.Message);
            return;
        }

        var open = await db.UpdateRuns.Where(r => r.State == ServerUpdateState.Downloading || r.State == ServerUpdateState.Staged).ToListAsync(ct);
        foreach (var run in open)
        {
            if (run.ToVersion == AppVersion.Current)
            {
                (run.State, run.Message, run.FinishedAt) = (ServerUpdateState.Succeeded, run.Message ?? "Działa nowa wersja.", now);
            }
            else if (run.State == ServerUpdateState.Downloading && _updateLock.CurrentCount > 0)
            {
                (run.State, run.Message, run.FinishedAt) = (ServerUpdateState.Failed, "Pobieranie zostało przerwane.", now);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<UpdateResult> FailRunAsync(Guid runId, string message, CancellationToken ct)
    {
        await SetRunStateAsync(runId, ServerUpdateState.Failed, message, finished: true, ct);
        return UpdateResult.Fail(message);
    }

    private async Task SetRunStateAsync(Guid runId, ServerUpdateState state, string message, bool finished, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.UpdateRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;
        run.State = state;
        run.Message = Truncate(message, 2000);
        if (finished) run.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
