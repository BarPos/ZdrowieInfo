namespace ZdrowieInfo.Server.Sources;

/// <summary>Jak często źródło jest sprawdzane (start o 5:00 czasu warszawskiego).</summary>
public enum SourceGroup
{
    /// <summary>Codziennie.</summary>
    Daily = 0,
    /// <summary>W poniedziałki.</summary>
    Weekly = 1,
    /// <summary>1. dnia miesiąca.</summary>
    Monthly = 2,
    /// <summary>1. dnia miesiąca nieparzystego (styczeń, marzec, …).</summary>
    Bimonthly = 3,
}

/// <summary>Metoda wykrywania zmian.</summary>
public enum SourceMethod
{
    /// <summary>Lista wpisów: porównanie tytułów i linków.</summary>
    List = 0,
    /// <summary>Cała strona: porównanie znormalizowanej treści.</summary>
    Page = 1,
}

public enum FetchMode
{
    Http = 0,
    /// <summary>Przeglądarka headless (na razie nieużywane – docs/decyzje.md, D4).</summary>
    Browser = 1,
    /// <summary>Źródło sprawdzane ręcznie – aplikacja go nie pobiera.</summary>
    Manual = 2,
}

public enum SourceStatus
{
    /// <summary>Jeszcze nie sprawdzone.</summary>
    New = 0,
    Ok = 1,
    /// <summary>Ostatnie sprawdzenie nieudane, zaplanowane ponowienie (+5 / +10 min).</summary>
    Retrying = 2,
    /// <summary>Otwarty incydent – „wymaga uwagi”.</summary>
    NeedsAttention = 3,
}

public enum CheckErrorType
{
    Http = 0,
    Timeout = 1,
    Network = 2,
    /// <summary>Blokada (np. Imperva Incapsula, 403/429).</summary>
    Blocked = 3,
    /// <summary>Selektor nie znalazł wpisów albo treści.</summary>
    NoItems = 4,
    RobotsDisallowed = 5,
    TooLarge = 6,
}

public enum CheckRunKind
{
    Baseline = 0,
    Scheduled = 1,
    Retry = 2,
    Manual = 3,
}

public enum CheckRunStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
}

public enum SourceItemKind
{
    /// <summary>Wpis z listy (artykuł, komunikat).</summary>
    Entry = 0,
    /// <summary>Zmiana treści strony (metoda „cała strona”); treść to diff.</summary>
    PageChange = 1,
}

/// <summary>Źródło (tabela <c>sources</c>). Lista źródeł to dane, nie kod.</summary>
public sealed class Source
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public SourceGroup Group { get; set; }
    public SourceMethod Method { get; set; }
    public SourceSelectors Selectors { get; set; } = new();
    public FetchMode FetchMode { get; set; }
    public bool IsActive { get; set; } = true;
    public SourceStatus Status { get; set; }
    public string? StatusMessage { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Do jakiego dnia wstecz przejrzano archiwum listy (raporty wstecz); <see cref="DateOnly.MinValue"/> – cała lista.
    /// Razem z <see cref="BaselineAt"/> mówi, za które dni znamy wszystkie wpisy źródła.
    /// </summary>
    public DateOnly? ArchiveFrom { get; set; }

    /// <summary>Pierwsza udana ekstrakcja (stan bazowy). Null = następne udane sprawdzenie zapisze stan bazowy bez wpisów „nowych”.</summary>
    public DateTimeOffset? BaselineAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public int ConsecutiveFailures { get; set; }

    /// <summary>Pierwsza nieudana próba w bieżącej serii błędów (początek incydentu).</summary>
    public DateTimeOffset? FailingSince { get; set; }

    /// <summary>Walidatory HTTP z ostatniej udanej ekstrakcji (czyszczone przy zmianie adresu, metody lub selektorów).</summary>
    public string? ETag { get; set; }
    public string? LastModified { get; set; }

    /// <summary>Metoda „cała strona”: hash i tekst ostatniej znanej treści (do diffu).</summary>
    public string? ContentHash { get; set; }
    public string? PageText { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Jedno pobranie źródła (tabela <c>source_snapshots</c>) – historia i diagnostyka.</summary>
public sealed class SourceSnapshot
{
    public long Id { get; set; }
    public int SourceId { get; set; }
    public long? CheckRunId { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public int? HttpStatus { get; set; }
    public bool NotModified { get; set; }
    public bool Succeeded { get; set; }
    public CheckErrorType? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ItemsFound { get; set; }
    public int NewItems { get; set; }
    public string? ContentHash { get; set; }
    public int DurationMs { get; set; }

    /// <summary>Ile stron listy pobrano (1 = tylko pierwsza).</summary>
    public int PagesFetched { get; set; } = 1;

    /// <summary>Ostrzeżenie przy udanym sprawdzeniu, np. możliwe pominięte wpisy (lista dłuższa niż limit stron).</summary>
    public string? Warning { get; set; }
}

/// <summary>Wykryty wpis (tabela <c>source_items</c>); unikalny (source_id, key).</summary>
public sealed class SourceItem
{
    public long Id { get; set; }
    public int SourceId { get; set; }
    public SourceItemKind Kind { get; set; }

    /// <summary>Klucz wpisu: znormalizowany bezwzględny URL (dla zmian strony: URL#data-czas).</summary>
    public required string Key { get; set; }
    public required string Title { get; set; }
    public required string Url { get; set; }
    public DateOnly? PublishedOn { get; set; }
    public string? Summary { get; set; }

    /// <summary>Treść do analizy (tekst wpisu albo diff strony). Usuwana po 12 miesiącach.</summary>
    public string? Content { get; set; }
    public string? ContentHash { get; set; }

    /// <summary>Wpis zastany przy stanie bazowym – nigdy nie trafia do raportu.</summary>
    public bool IsBaseline { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>Ostatnia zmiana istotna dla raportu: pojawienie się albo aktualizacja (zmiana tytułu/daty na liście).</summary>
    public DateTimeOffset LastChangedAt { get; set; }
    public int UpdateCount { get; set; }
    public string? PreviousTitle { get; set; }

    public DateTimeOffset? DetailFetchedAt { get; set; }
    public int DetailAttempts { get; set; }
    public string? DetailError { get; set; }

    /// <summary>
    /// Kiedy wpis trafił do raportu (albo został oznaczony jako zaraportowany poleceniem --mark-reported-before).
    /// Null = czeka na raport. Aktualizacja wpisu zeruje te pola, więc wraca do następnego raportu.
    /// </summary>
    public DateTimeOffset? ReportedAt { get; set; }
    public long? ReportId { get; set; }
}

/// <summary>Problem z dostępem do źródła (tabela <c>source_incidents</c>), przechowywany bezterminowo.</summary>
public sealed class SourceIncident
{
    public long Id { get; set; }
    public int SourceId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public CheckErrorType ErrorType { get; set; }
    public string? Details { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset LastAttemptAt { get; set; }
    public bool FailurePushSent { get; set; }
    public bool RecoveryPushSent { get; set; }
}

/// <summary>Jedno uruchomienie sprawdzania (tabela <c>check_runs</c>).</summary>
public sealed class CheckRun
{
    public long Id { get; set; }
    public DateOnly Date { get; set; }
    public CheckRunKind Kind { get; set; }
    public List<SourceGroup> Groups { get; set; } = [];
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public CheckRunStatus Status { get; set; }
    public int SourcesChecked { get; set; }
    public int SourcesFailed { get; set; }
    public int NewItems { get; set; }
    public string? Error { get; set; }
}
