using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Reports;

namespace ZdrowieInfo.Server.Export;

public sealed record ExportResult(bool Success, string? TexPath, string? PdfPath, string? Error);

/// <summary>
/// .tex i PDF raportu w &lt;DataDirectory&gt;/reports/&lt;rok&gt;/raport-&lt;data&gt;.{tex,pdf}; ścieżki i ewentualny błąd w raporcie.
/// Błąd kompilacji nie blokuje raportu ani powiadomienia – raport HTML jest dostępny od razu.
/// </summary>
public sealed class ReportExporter(
    IDbContextFactory<ZdrowieInfoDbContext> dbFactory,
    LatexReportRenderer renderer,
    PdfCompiler compiler,
    IOptions<ZdrowieInfoOptions> app,
    ILogger<ReportExporter> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public Task<ExportResult> ExportAsync(DateOnly date, CancellationToken ct = default) => ExportAsync(ReportKey.Daily(date), ct);

    /// <summary>Kompilacje po kolei (jedna naraz) – także przy pobieraniu przez publiczny link.</summary>
    public async Task<ExportResult> ExportAsync(ReportKey key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var view = await ReportView.LoadAsync(db, key, ct);
            if (view is null) return new ExportResult(false, null, null, "Nie ma takiego raportu.");

            var folder = Directory.CreateDirectory(Path.Combine(app.Value.DataDirectory, "reports", key.End.Year.ToString())).FullName;
            var texPath = Path.Combine(folder, $"raport-{key.Slug}.tex");
            var pdfPath = Path.Combine(folder, $"raport-{key.Slug}.pdf");

            string tex;
            try
            {
                tex = renderer.Render(view);
            }
            catch (Exception e) when (e is InvalidOperationException or Scriban.Syntax.ScriptRuntimeException)
            {
                await SaveStateAsync(db, view.Report.Id, null, null, e.Message, ct);
                return new ExportResult(false, null, null, e.Message);
            }
            await File.WriteAllTextAsync(texPath, tex, ct);

            var pdf = await compiler.CompileAsync(tex, ct);
            if (pdf.Success)
            {
                await File.WriteAllBytesAsync(pdfPath, pdf.Pdf!, ct);
                await SaveStateAsync(db, view.Report.Id, texPath, pdfPath, null, ct);
                logger.LogInformation("Raport {Key}: PDF {Size} KB.", key, pdf.Pdf!.Length / 1024);
                return new ExportResult(true, texPath, pdfPath, null);
            }

            if (File.Exists(pdfPath)) File.Delete(pdfPath); // nieaktualny PDF poprzedniej wersji raportu
            await SaveStateAsync(db, view.Report.Id, texPath, null, pdf.Error, ct);
            return new ExportResult(false, texPath, null, pdf.Error);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Task SaveStateAsync(ZdrowieInfoDbContext db, long reportId, string? tex, string? pdf, string? error, CancellationToken ct) =>
        db.Reports.Where(r => r.Id == reportId).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.TexPath, tex)
            .SetProperty(r => r.PdfPath, pdf)
            .SetProperty(r => r.PdfError, error), ct);
}
