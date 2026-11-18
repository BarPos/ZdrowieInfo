namespace ZdrowieInfo.Server.Sources;

/// <summary>Sekcja <c>ZdrowieInfo:Sources</c>.</summary>
public sealed class SourcesOptions
{
    public const string Section = "ZdrowieInfo:Sources";

    /// <summary>Adres kontaktowy dopisywany do User-Agenta (dla administratorów stron). Pusty = bez adresu.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>Godzina startu sprawdzania (czas warszawski).</summary>
    public TimeOnly DailyCheckTime { get; set; } = new(5, 0);

    /// <summary>Wyłączenie harmonogramu (testy, praca nad selektorami).</summary>
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>Odstęp między żądaniami do tej samej domeny: losowo od Min do Max.</summary>
    public TimeSpan MinDelayPerHost { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelayPerHost { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Pierwsza przerwa przed ponowieniem nieudanego żądania (5xx, 408, 429, błąd sieci); dalej wykładniczo, 2 ponowienia.</summary>
    public TimeSpan HttpRetryDelay { get; set; } = TimeSpan.FromSeconds(3);
    public int MaxResponseBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Ile stron pojedynczych wpisów pobierać na jedno źródło w jednym sprawdzeniu (reszta przy następnym).</summary>
    public int MaxDetailFetchesPerSource { get; set; } = 100;

    /// <summary>Ile stron listy (łącznie z pierwszą i zapasową) przejrzeć w jednym sprawdzeniu, zanim pojawi się ostrzeżenie.</summary>
    public int MaxListPages { get; set; } = 10;

    /// <summary>Raport wstecz: ile stron listy jednego źródła przejrzeć, szukając wpisów z wcześniejszych dni.</summary>
    public int MaxArchivePages { get; set; } = 40;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);
    public int FailuresBeforeIncident { get; set; } = 3;
    public TimeSpan IncidentRecheckInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Po tym czasie treść wpisów jest usuwana (cytaty w raportach zostają).</summary>
    public TimeSpan ItemContentRetention { get; set; } = TimeSpan.FromDays(365);
}
