using MudBlazor;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Sources;

namespace ZdrowieInfo.Web.Admin;

/// <summary>Polskie nazwy i formatowanie w panelu.</summary>
public static class Labels
{
    public static string Of(SourceGroup g) => g switch
    {
        SourceGroup.Daily => "codziennie",
        SourceGroup.Weekly => "tygodniowo (pon.)",
        SourceGroup.Monthly => "miesięcznie (1.)",
        SourceGroup.Bimonthly => "co 2 miesiące",
        _ => g.ToString(),
    };

    public static string Of(SourceMethod m) => m == SourceMethod.List ? "lista wpisów" : "cała strona";

    public static string Of(FetchMode m) => m switch
    {
        FetchMode.Http => "HTTP",
        FetchMode.Browser => "przeglądarka",
        FetchMode.Manual => "ręcznie",
        _ => m.ToString(),
    };

    public static string Of(SourceStatus s) => s switch
    {
        SourceStatus.New => "nowe",
        SourceStatus.Ok => "OK",
        SourceStatus.Retrying => "ponawianie",
        SourceStatus.NeedsAttention => "wymaga uwagi",
        _ => s.ToString(),
    };

    public static Color ColorOf(SourceStatus s) => s switch
    {
        SourceStatus.Ok => Color.Success,
        SourceStatus.Retrying => Color.Warning,
        SourceStatus.NeedsAttention => Color.Error,
        _ => Color.Default,
    };

    public static string Of(CheckErrorType? e) => e switch
    {
        null => "",
        CheckErrorType.Http => "błąd HTTP",
        CheckErrorType.Timeout => "przekroczony czas",
        CheckErrorType.Network => "błąd sieci",
        CheckErrorType.Blocked => "blokada",
        CheckErrorType.NoItems => "0 wpisów",
        CheckErrorType.RobotsDisallowed => "robots.txt",
        CheckErrorType.TooLarge => "za duża strona",
        _ => e.ToString()!,
    };

    public static string Of(CheckRunKind k) => k switch
    {
        CheckRunKind.Baseline => "stan bazowy",
        CheckRunKind.Scheduled => "planowe",
        CheckRunKind.Retry => "ponowienie",
        CheckRunKind.Manual => "ręczne",
        _ => k.ToString(),
    };

    public static string Of(CheckRunStatus s) => s switch
    {
        CheckRunStatus.Running => "w toku",
        CheckRunStatus.Completed => "zakończone",
        CheckRunStatus.Failed => "błąd",
        _ => s.ToString(),
    };

    public static string Time(DateTimeOffset? t) => t is null ? "—" : WarsawClock.ToLocal(t.Value).ToString("dd.MM.yyyy HH:mm");

    public static string Date(DateOnly? d) => d?.ToString("dd.MM.yyyy") ?? "—";

    public static string Duration(TimeSpan d) =>
        d.TotalMinutes < 1 ? $"{d.TotalSeconds:0} s" : d.TotalHours < 1 ? $"{d.TotalMinutes:0} min" : d.TotalDays < 1 ? $"{d.TotalHours:0.#} h" : $"{d.TotalDays:0.#} dni";
}
