using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ZdrowieInfo.Server.Updates;

/// <summary>Parser <c>SHA256SUMS.txt</c> (wynik <c>sha256sum</c>: <c>&lt;hex&gt;  &lt;nazwa&gt;</c> albo <c>&lt;hex&gt; *&lt;nazwa&gt;</c>).</summary>
public static partial class Sha256Sums
{
    public const int MaxBytes = 64 * 1024;

    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = LineRegex().Match(line);
            if (!m.Success) continue;
            var name = m.Groups[2].Value;
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            result[name] = m.Groups[1].Value.ToLowerInvariant();
        }
        return result;
    }

    [GeneratedRegex(@"^([0-9a-fA-F]{64}) [ *](\S.*)$")]
    private static partial Regex LineRegex();
}

internal static class FileHashing
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Kopiuje strumień do nowego pliku, licząc po drodze SHA-256 i pilnując limitu rozmiaru (strumień nie musi
    /// obsługiwać przewijania). Zwraca (rozmiar, sha256 małymi literami, pierwsze 8 bajtów). Przy błędzie usuwa plik.
    /// </summary>
    public static async Task<(long Size, string Sha256, byte[] Head)> CopyAndHashAsync(Stream source, string destination,
        long maxBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var head = new byte[8];
        var headLength = 0;
        long total = 0;
        try
        {
            await using (var fs = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > maxBytes) throw new InvalidDataException($"Plik jest większy niż {maxBytes / (1024 * 1024)} MiB.");
                    if (headLength < head.Length)
                    {
                        var take = Math.Min(head.Length - headLength, n);
                        Array.Copy(buffer, 0, head, headLength, take);
                        headLength += take;
                    }
                    hash.AppendData(buffer, 0, n);
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                await fs.FlushAsync(ct);
            }
            return (total, Convert.ToHexStringLower(hash.GetHashAndReset()), head[..headLength]);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    public static async Task<string> ReadSmallTextAsync(Stream source, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > maxBytes) throw new InvalidDataException("Plik jest za duży.");
            ms.Write(buffer, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static void TryDeleteDirectory(string path)
    {
        try { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Pliki wymieniane z <c>deploy/linux/update.sh</c> w <c>&lt;DataDirectory&gt;/updates/</c>: tekst UTF-8, <c>klucz=wartość</c>
/// w liniach, komentarze <c>#</c>; update.sh czyta je bez eval i sprawdza każdą wartość. Serwer zapisuje zlecenie
/// atomowo (plik tymczasowy + zmiana nazwy), bo jednostka <c>zdrowieinfo-update.path</c> reaguje na samo pojawienie się
/// <c>request.conf</c>.
/// <code>
/// request.conf (serwer → update.sh)      result.conf (update.sh → serwer)
///   format=1                               format=1
///   run_id=&lt;guid&gt;                         run_id=&lt;guid&gt;
///   version=1.2.0                          version=1.2.0
///   from_version=1.1.0                     from_version=1.1.0
///   arch=x64                               state=Succeeded|RolledBack|Failed
///   file=ZdrowieInfo.Server-1.2.0-linux-x64.tar.gz   message=&lt;jedna linia&gt;
///   sha256=&lt;64 hex&gt;                       finished_at=2026-01-01T00:00:00Z
/// </code>
/// Paczka i jej SHA256SUMS.txt leżą w <c>updates/&lt;wersja&gt;/</c>.
/// </summary>
public static class UpdateRequestFile
{
    public const string RequestFileName = "request.conf";
    public const string ResultFileName = "result.conf";
    public const int Format = 1;

    public static string BuildRequest(Guid runId, string version, string fromVersion, string arch, string file, string sha256)
    {
        var sb = new StringBuilder();
        sb.Append("# Zlecenie aktualizacji ZdrowieInfo. Zapisuje serwer, wykonuje update.sh.\n");
        sb.Append("format=").Append(Format.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("run_id=").Append(runId.ToString("D")).Append('\n');
        sb.Append("version=").Append(version).Append('\n');
        sb.Append("from_version=").Append(fromVersion).Append('\n');
        sb.Append("arch=").Append(arch).Append('\n');
        sb.Append("file=").Append(file).Append('\n');
        sb.Append("sha256=").Append(sha256).Append('\n');
        return sb.ToString();
    }

    public static Dictionary<string, string> ParseKeyValues(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            result[line[..eq].Trim()] = line[(eq + 1)..];
        }
        return result;
    }
}
