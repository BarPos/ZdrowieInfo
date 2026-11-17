namespace ZdrowieInfo.Server.Updates;

public sealed record ReleaseInfo(string Version, string TagName, string? Name, string? Notes, DateTimeOffset? PublishedAt, bool Prerelease, string? Url);

/// <summary>Jeden plik wydania.</summary>
public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl);

/// <summary>Wydanie z plikami.</summary>
public sealed record Release(ReleaseInfo Info, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? FindAsset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>Skąd przychodzą wydania: GitHub (produkcja) albo fałszywe źródło w testach. Nieskonfigurowane – zero ruchu sieciowego.</summary>
public interface IReleaseSource
{
    bool IsConfigured { get; }

    /// <summary>Najnowsze wydanie wybranego kanału albo null.</summary>
    Task<Release?> GetLatestAsync(CancellationToken ct = default);

    /// <summary>Strumień pliku wydania zwróconego przez to źródło.</summary>
    Task<Stream> OpenAssetAsync(ReleaseAsset asset, CancellationToken ct = default);
}

public sealed class ReleaseSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Nazwy plików wydania – te same w build-server.sh, workflow release, install.sh i update.sh.</summary>
public static class ReleaseAssetNames
{
    public const string Sums = "SHA256SUMS.txt";
    public const string InstallScript = "install.sh";

    /// <summary><c>ZdrowieInfo.Server-&lt;wersja&gt;-linux-&lt;arch&gt;.tar.gz</c>, arch <c>x64</c> albo <c>arm64</c>.</summary>
    public static string ServerTarball(string version, string arch) => $"ZdrowieInfo.Server-{version}-linux-{arch}.tar.gz";

    public static string Tag(string version) => "v" + version;
}
