namespace ZdrowieInfo.Server.Updates;

/// <summary>Sekcja <c>ZdrowieInfo:Updates</c> – skąd brać nowe wersje (wydania GitHub).</summary>
public sealed class UpdatesOptions
{
    public const string Section = "ZdrowieInfo:Updates";

    /// <summary>
    /// Repozytorium „właściciel/nazwa”, z którego wydań korzystają install.sh i aktualizacja w panelu (wpisywane przez
    /// install.sh). Puste = aktualizacje wyłączone: serwer nie łączy się z GitHubem.
    /// </summary>
    public string GitHubRepository { get; set; } = "";

    /// <summary>stable (domyślnie, tylko wydania) albo prerelease (także wydania próbne).</summary>
    public string Channel { get; set; } = "stable";

    /// <summary>Co ile godzin sprawdzać, czy jest nowa wersja.</summary>
    public int CheckIntervalHours { get; set; } = 24;

    /// <summary>Token GitHub (tylko dla prywatnego repozytorium; odczyt wydań).</summary>
    public string GitHubToken { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GitHubRepository);
}
