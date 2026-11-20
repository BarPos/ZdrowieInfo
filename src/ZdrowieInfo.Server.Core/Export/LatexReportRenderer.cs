using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Options;
using Scriban;
using Scriban.Runtime;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Reports;

namespace ZdrowieInfo.Server.Export;

/// <summary>
/// Plik .tex z raportu: szablon Scriban (wbudowany albo własna formatka z <see cref="ExportOptions.TemplatePath"/>).
/// Claude nie pisze LaTeX-a – kompilacja nie zależy od modelu. Każdy tekst przez funkcję <c>latex</c>.
/// </summary>
public sealed class LatexReportRenderer(IOptions<ExportOptions> options, TimeProvider time)
{
    private static readonly Lazy<string> BuiltIn = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZdrowieInfo.Server.Export.report.tex.scriban")
            ?? throw new InvalidOperationException("Brak wbudowanego szablonu report.tex.scriban.");
        return new StreamReader(stream).ReadToEnd();
    });

    public string Render(ReportView view)
    {
        string source;
        try
        {
            source = options.Value.TemplatePath is { Length: > 0 } path ? File.ReadAllText(path) : BuiltIn.Value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Nie można odczytać formatki {options.Value.TemplatePath}: {e.Message}", e);
        }
        var template = Template.Parse(source);
        if (template.HasErrors)
            throw new InvalidOperationException("Błąd w szablonie LaTeX: " + string.Join("; ", template.Messages));

        var model = new ScriptObject();
        model.Import("latex", new Func<string?, string>(LatexText.Escape));
        model.Import("latex_url", new Func<string?, string>(LatexText.Url));
        model.Import("latex_line", new Func<string?, string>(LatexText.EscapeLine));

        var report = view.Report;
        var pl = CultureInfo.GetCultureInfo("pl-PL");
        model["title"] = report.Title;
        model["summary"] = report.Summary;
        model["date_long"] = view.DateLong;
        model["kind_label"] = view.KindLabel;
        model["date_short"] = report.Date.ToString("dd.MM.yyyy");
        model["producer"] = view.ProducerLabel;
        model["status_note"] = view.StatusNote;
        model["is_ai_error"] = report.Status == ReportStatus.AiError;
        model["findings_count"] = report.Findings.Count;
        model["critical_count"] = report.Findings.Count(f => f.Importance == Importance.Critical);
        model["entries_count"] = view.Entries.Count;
        model["sources_checked"] = view.SourcesChecked;
        model["sources_total"] = report.SourcesState.Count;
        model["generated"] = WarsawClock.ToLocal(time.GetUtcNow()).ToString("dd.MM.yyyy HH:mm", pl);
        // Każdy punkt z własną ważnością nad kategorią (jak na stronie), od najważniejszych.
        model["findings"] = view.Findings.Select(f => new ScriptObject
        {
            ["number"] = f.Number,
            ["importance"] = f.Importance,
            ["color"] = f.Finding.Importance switch { Importance.Critical => "critical", Importance.High => "high", _ => "muted" },
            ["headline"] = f.Finding.Headline,
            ["category"] = f.Category,
            ["body"] = f.Finding.Body,
            ["action"] = f.Finding.Action,
            ["citations"] = f.Citations.Select(c => new ScriptObject
            {
                ["quote"] = c.Citation.Quote,
                ["title"] = c.Citation.Title,
                ["url"] = c.Citation.Url,
                ["date"] = c.Citation.PublishedOn?.ToString("dd.MM.yyyy"),
                ["verified"] = c.Citation.Verified,
            }).ToList(),
        }).ToList();
        // Te same linki co na stronie: pominięte wpisy, źródła z uwagami i wpisy źródłowe.
        model["ignored"] = view.Ignored.Select(i => new ScriptObject { ["title"] = i.Title, ["url"] = i.Url ?? "", ["reason"] = i.Reason }).ToList();
        model["source_problems"] = view.SourceProblems.Select(s => new ScriptObject { ["name"] = s.Name, ["url"] = s.Url, ["text"] = s.Problem ?? s.Warning }).ToList();
        model["recoveries"] = view.Recoveries.ToList();
        model["research"] = view.Research is { } research
            ? new ScriptObject
            {
                ["notes"] = research.Notes,
                ["searches"] = research.Searches,
                ["sources"] = research.Sources.Select(s => new ScriptObject { ["title"] = s.Title, ["url"] = s.Url }).ToList(),
            }
            : null;
        model["entries"] = view.Entries.Select(e => new ScriptObject
        {
            ["title"] = e.Title, ["url"] = e.Url, ["source"] = e.Source, ["date"] = e.PublishedOn?.ToString("dd.MM.yyyy"),
        }).ToList();

        var context = new TemplateContext { StrictVariables = true, MemberRenamer = m => m.Name };
        context.PushGlobal(model);
        return template.Render(context);
    }
}
