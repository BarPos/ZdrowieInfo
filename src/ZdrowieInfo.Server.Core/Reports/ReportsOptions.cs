namespace ZdrowieInfo.Server.Reports;

/// <summary>Sekcja <c>ZdrowieInfo:Reports</c>.</summary>
public sealed class ReportsOptions
{
    public const string Section = "ZdrowieInfo:Reports";

    /// <summary>Claude albo Sample (przykładowy raport bez API – praca lokalna i testy).</summary>
    public string Generator { get; set; } = "Claude";

    /// <summary>Od tej godziny (czas warszawski) raport dnia jest należny.</summary>
    public TimeOnly ReportTime { get; set; } = new(5, 30);

    /// <summary>Do tej godziny raport czeka, aż sprawdzanie źródeł i ponowienia się zakończą.</summary>
    public TimeOnly WaitForChecksUntil { get; set; } = new(6, 0);

    /// <summary>Błąd API: ponowienie co tyle…</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>…do tej godziny; potem raport awaryjny z listą wpisów bez analizy.</summary>
    public TimeOnly RetryUntil { get; set; } = new(6, 20);

    public int MaxOutputTokens { get; set; } = 16_000;

    /// <summary>
    /// Sprawdzanie podatności w internecie (przed analizą) trwa najwyżej tyle – potem raport powstaje bez niego, z uwagą.
    /// Czas rzeczywisty: wyszukiwanie nie może przesunąć porannego raportu poza okno ponowień.
    /// </summary>
    public TimeSpan ResearchTimeout { get; set; } = TimeSpan.FromMinutes(3);
}
