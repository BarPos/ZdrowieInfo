using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;

namespace ZdrowieInfo.Server.Export;

public sealed record PdfResult(bool Success, byte[]? Pdf, string? Error);

/// <summary>
/// Kompilacja .tex → PDF programem systemowym (latexmk -xelatex albo tectonic): zawsze bez shell-escape, w katalogu
/// tymczasowym, z limitem czasu, jako ten sam nieuprzywilejowany użytkownik co usługa.
/// </summary>
public sealed class PdfCompiler(IOptions<ExportOptions> options, IOptions<ZdrowieInfoOptions> app, ILogger<PdfCompiler> logger)
{
    public async Task<PdfResult> CompileAsync(string tex, CancellationToken ct = default)
    {
        var o = options.Value;
        var work = Directory.CreateDirectory(Path.Combine(app.Value.DataDirectory, "tmp", "latex-" + Guid.NewGuid().ToString("N")[..12])).FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "raport.tex"), tex, ct);
            var start = new ProcessStartInfo
            {
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (string.Equals(o.Compiler, "tectonic", StringComparison.OrdinalIgnoreCase))
            {
                start.FileName = "tectonic";
                foreach (var a in new[] { "--untrusted", "--keep-logs", "raport.tex" }) start.ArgumentList.Add(a);
                start.Environment["TECTONIC_CACHE_DIR"] = Path.Combine(app.Value.DataDirectory, "tectonic-cache");
            }
            else
            {
                start.FileName = "latexmk";
                foreach (var a in new[] { "-xelatex", "-no-shell-escape", "-interaction=nonstopmode", "-halt-on-error", "-file-line-error", "raport.tex" })
                    start.ArgumentList.Add(a);
                // Cache czcionek i formatów w katalogu danych (usługa ma ProtectSystem=strict).
                start.Environment["TEXMFVAR"] = Path.Combine(app.Value.DataDirectory, "texmf-var");
            }

            using var process = new Process { StartInfo = start };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                return new PdfResult(false, null, $"Nie znaleziono programu {start.FileName} – zainstaluj TeX Live (xelatex, latexmk) albo Tectonic. ({e.Message})");
            }

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(o.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return new PdfResult(false, null, $"Kompilacja przerwana po {o.Timeout.TotalSeconds:0} s.");
            }
            await Task.WhenAll(stdout, stderr);

            var pdfPath = Path.Combine(work, "raport.pdf");
            if (process.ExitCode == 0 && File.Exists(pdfPath))
                return new PdfResult(true, await File.ReadAllBytesAsync(pdfPath, ct), null);

            var log = Path.Combine(work, "raport.log");
            var details = File.Exists(log) ? ErrorLines(await File.ReadAllLinesAsync(log, ct)) : (stderr.Result + stdout.Result).Trim();
            logger.LogError("Kompilacja PDF nieudana ({Exit}): {Details}", process.ExitCode, details);
            return new PdfResult(false, null, $"Błąd kompilacji LaTeX: {details}");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Z logu LaTeX-a: linie błędów („! …” albo „plik:linia: …”) z kontekstem, najwyżej kilka.</summary>
    private static string ErrorLines(string[] log)
    {
        var errors = log.Select((l, i) => (l, i)).Where(x => x.l.StartsWith('!') || x.l.Contains(".tex:", StringComparison.Ordinal))
            .Take(3).Select(x => string.Join(" ", log.Skip(x.i).Take(3)).Trim()).ToList();
        return errors.Count > 0 ? string.Join(" | ", errors) : string.Join(" ", log.TakeLast(5));
    }
}
