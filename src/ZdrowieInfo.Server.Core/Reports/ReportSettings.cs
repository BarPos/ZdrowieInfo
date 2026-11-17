using ZdrowieInfo.Server.Settings;

namespace ZdrowieInfo.Server.Reports;

/// <summary>Profil przychodni – trafia do promptu przy każdym raporcie (edytowany w panelu).</summary>
public sealed record ClinicProfile
{
    public static readonly IReadOnlyList<string> KnownServices = ["POZ", "AOS", "stomatologia", "programy profilaktyczne", "DiLO"];

    public List<string> Services { get; init; } = [.. KnownServices];

    /// <summary>Podmiot kluczowy w rozumieniu ustawy o krajowym systemie cyberbezpieczeństwa.</summary>
    public bool KeyEntityKsc { get; init; } = true;

    /// <summary>Systemy używane w przychodni (gabinetowy, firewall, serwery, VPN…), po jednym w linii.</summary>
    public string Systems { get; init; } = "";

    public string? Notes { get; init; }
}

/// <summary>Ustawienia wywołania Claude (panel). Klucz API osobno, zaszyfrowany.</summary>
public sealed record ClaudeSettings
{
    public string Model { get; init; } = "claude-sonnet-5-5";

    /// <summary>low | medium | high | xhigh | max</summary>
    public string Effort { get; init; } = "medium";
}

/// <summary>Stan prób przygotowania raportu na dany dzień (ponowienia co 10 minut do 6:20).</summary>
public sealed record ReportAttempts(DateOnly Date, int Attempts, DateTimeOffset LastAttemptAt, string? LastError);

public sealed class ReportSettings(AppSettingsStore store)
{
    private const string ProfileKey = "clinic.profile";
    private const string ClaudeKey = "claude.settings";
    private const string ApiKeyKey = "claude.api_key";
    private const string AttemptsKey = "reports.attempts";
    private const string SkipKey = "reports.skip_date";

    public async Task<ClinicProfile> GetProfileAsync(CancellationToken ct = default) => await store.GetAsync<ClinicProfile>(ProfileKey, ct) ?? new();
    public Task SetProfileAsync(ClinicProfile profile, CancellationToken ct = default) => store.SetAsync(ProfileKey, profile, ct: ct);

    public async Task<ClaudeSettings> GetClaudeAsync(CancellationToken ct = default) => await store.GetAsync<ClaudeSettings>(ClaudeKey, ct) ?? new();
    public Task SetClaudeAsync(ClaudeSettings settings, CancellationToken ct = default) => store.SetAsync(ClaudeKey, settings, ct: ct);

    public Task<string?> GetApiKeyAsync(CancellationToken ct = default) => store.GetAsync<string>(ApiKeyKey, ct);
    public Task SetApiKeyAsync(string key, CancellationToken ct = default) => store.SetAsync(ApiKeyKey, key.Trim(), isProtected: true, ct);
    public Task ClearApiKeyAsync(CancellationToken ct = default) => store.RemoveAsync(ApiKeyKey, ct);

    public Task<ReportAttempts?> GetAttemptsAsync(CancellationToken ct = default) => store.GetAsync<ReportAttempts>(AttemptsKey, ct);
    public Task SetAttemptsAsync(ReportAttempts attempts, CancellationToken ct = default) => store.SetAsync(AttemptsKey, attempts, ct: ct);

    /// <summary>Dzień, na który harmonogram ma nie przygotowywać raportu (administrator usunął dzisiejszy raport).</summary>
    public async Task<DateOnly?> GetSkipDateAsync(CancellationToken ct = default) => (await store.GetAsync<SkipDate>(SkipKey, ct))?.Date;
    public Task SetSkipDateAsync(DateOnly date, CancellationToken ct = default) => store.SetAsync(SkipKey, new SkipDate(date), ct: ct);

    private sealed record SkipDate(DateOnly Date);
}
