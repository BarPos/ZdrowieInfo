namespace ZdrowieInfo.Server.Export;

/// <summary>Sekcja <c>ZdrowieInfo:Export</c> – dokument LaTeX i PDF raportu.</summary>
public sealed class ExportOptions
{
    public const string Section = "ZdrowieInfo:Export";

    /// <summary>Czy po każdym raporcie generować .tex i PDF.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>latexmk (TeX Live, -xelatex) albo tectonic.</summary>
    public string Compiler { get; set; } = "latexmk";

    /// <summary>Własna formatka (Scriban). Puste = szablon wbudowany.</summary>
    public string? TemplatePath { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}
