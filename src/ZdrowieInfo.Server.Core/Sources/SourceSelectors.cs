namespace ZdrowieInfo.Server.Sources;

/// <summary>
/// Selektory CSS zapisane przy źródle (kolumna jsonb). Dla metody „lista”: Item + Link (+ Title, Date, Summary);
/// dla „cała strona”: Content. Detail* dotyczy strony pojedynczego wpisu.
/// </summary>
public sealed record SourceSelectors
{
    /// <summary>Format listy: HTML (selektory CSS) albo JSON (nazwy pól; Item = ścieżka do tablicy, np. <c>articleDTOList</c>).</summary>
    public ListFormat Format { get; init; } = ListFormat.Html;

    /// <summary>Element jednego wpisu na liście.</summary>
    public string? Item { get; init; }

    /// <summary>Tytuł (w obrębie wpisu). Domyślnie tekst linku.</summary>
    public string? Title { get; init; }

    /// <summary>Link do wpisu (w obrębie wpisu). Domyślnie pierwszy <c>a[href]</c>.</summary>
    public string? Link { get; init; }

    /// <summary>Data publikacji (w obrębie wpisu).</summary>
    public string? Date { get; init; }

    /// <summary>Atrybut z datą (np. <c>datetime</c> w <c>&lt;time&gt;</c>); domyślnie tekst elementu.</summary>
    public string? DateAttribute { get; init; }

    /// <summary>Krótki opis na liście.</summary>
    public string? Summary { get; init; }

    /// <summary>Szablon adresu wpisu, gdy lista podaje tylko identyfikator, np. <c>https://ezdrowie.gov.pl/portal/artykul/{0}</c>.</summary>
    public string? LinkTemplate { get; init; }

    /// <summary>
    /// Link do następnej strony listy (selektor CSS w obrębie strony). Gdy ostatni wpis na stronie jest nowy,
    /// sprawdzanie przechodzi na kolejne strony, aż trafi na znany wpis – nic nie ginie przy wielu nowościach naraz.
    /// </summary>
    public string? NextPage { get; init; }

    /// <summary>
    /// Lista zapasowa ze stronicowaniem, gdy główna lista nie ma kolejnych stron (np. blok „10 najnowszych”), a wszystkie
    /// wpisy na niej są nowe. Te same selektory; brane są wpisy od daty ostatniego udanego sprawdzenia.
    /// </summary>
    public string? OverflowUrl { get; init; }

    /// <summary>Metoda „cała strona”: główna treść (kilka selektorów po przecinku – łączone).</summary>
    public string? Content { get; init; }

    /// <summary>Elementy usuwane przed ekstrakcją treści strony (baner cookies, przyciski, data aktualizacji…).</summary>
    public List<string> Remove { get; init; } = [];

    /// <summary>Treść na stronie pojedynczego wpisu. Domyślnie <c>main</c>, <c>article</c> albo <c>body</c>.</summary>
    public string? DetailContent { get; init; }

    public List<string> DetailRemove { get; init; } = [];
}

public enum ListFormat
{
    Html = 0,
    Json = 1,
}
