using System.Runtime.InteropServices;

namespace ZdrowieInfo.Server.Updates;

/// <summary>Co działający serwer wie o swojej instalacji (podmieniane w testach).</summary>
public interface IServerInstallation
{
    /// <summary><c>x64</c>, <c>arm64</c> albo null (nieobsługiwana architektura).</summary>
    string? Architecture { get; }
    bool SelfUpdateSupported { get; }
    /// <summary>Dlaczego aktualizacja z panelu nie działa (pokazywane w panelu) albo null.</summary>
    string? UnsupportedReason { get; }
}

/// <summary>
/// Aktualizacja z panelu wymaga: Linux x64/arm64, Ubuntu 24.04 lub nowszy i układu z install.sh – proces działa
/// z <c>/opt/zdrowieinfo/releases/&lt;wersja&gt;/</c>, <c>/opt/zdrowieinfo/current</c> to dowiązanie, a jednostka
/// <c>zdrowieinfo-update.path</c> jest zainstalowana.
/// </summary>
public sealed class ServerInstallation : IServerInstallation
{
    public const string InstallRoot = "/opt/zdrowieinfo";
    public const string UpdatePathUnit = "/etc/systemd/system/zdrowieinfo-update.path";

    private readonly Lazy<(bool Supported, string? Reason)> _probe;

    public ServerInstallation()
    {
        Architecture = RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => null,
        };
        _probe = new Lazy<(bool, string?)>(Probe);
    }

    public string? Architecture { get; }
    public bool SelfUpdateSupported => _probe.Value.Supported;
    public string? UnsupportedReason => _probe.Value.Reason;

    private (bool, string?) Probe()
    {
        if (!OperatingSystem.IsLinux()) return (false, "Aktualizacja z panelu działa tylko na Ubuntu (instalacja przez install.sh).");
        if (Architecture is null) return (false, $"Nieobsługiwana architektura procesora {RuntimeInformation.OSArchitecture}.");
        if (!IsUbuntu2404OrNewer()) return (false, "Aktualizacja z panelu wymaga Ubuntu 24.04 LTS lub nowszego.");
        var releases = Path.Combine(InstallRoot, "releases") + Path.DirectorySeparatorChar;
        var currentDir = Path.Combine(InstallRoot, "current") + Path.DirectorySeparatorChar;
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
        if (!baseDir.StartsWith(releases, StringComparison.Ordinal) && !baseDir.StartsWith(currentDir, StringComparison.Ordinal))
            return (false, $"Serwer nie działa z {releases} (nie zainstalowano go przez install.sh).");
        string? linkTarget;
        try { linkTarget = new FileInfo(Path.Combine(InstallRoot, "current")).LinkTarget; }
        catch (IOException) { linkTarget = null; }
        if (linkTarget is null) return (false, $"{InstallRoot}/current nie jest dowiązaniem (nie zainstalowano przez install.sh).");
        if (!File.Exists(UpdatePathUnit)) return (false, "Brak jednostki zdrowieinfo-update.path – uruchom install.sh --repair.");
        return (true, null);
    }

    private static bool IsUbuntu2404OrNewer()
    {
        try
        {
            if (!File.Exists("/etc/os-release")) return false;
            var values = UpdateRequestFile.ParseKeyValues(File.ReadAllText("/etc/os-release"));
            if (!values.TryGetValue("ID", out var id) || id.Trim('"') != "ubuntu") return false;
            return values.TryGetValue("VERSION_ID", out var v) && Version.TryParse(v.Trim('"'), out var version) && version >= new Version(24, 4);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
