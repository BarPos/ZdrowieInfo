using System.Text;

namespace ZdrowieInfo.Server.Export;

/// <summary>Bezpieczne wstawianie tekstu do LaTeX-a. Treść raportu pochodzi z modelu i ze stron – nigdy nie trafia do dokumentu bez tego.</summary>
public static class LatexText
{
    /// <summary>Znaki specjalne LaTeX-a jako zwykły tekst; nowe linie jako akapity.</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 16);
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            sb.Append(ch switch
            {
                '\\' => @"\textbackslash{}",
                '{' => @"\{",
                '}' => @"\}",
                '$' => @"\$",
                '&' => @"\&",
                '#' => @"\#",
                '^' => @"\textasciicircum{}",
                '_' => @"\_",
                '%' => @"\%",
                '~' => @"\textasciitilde{}",
                '"' => @"\textquotedbl{}", // w babel-polish " jest znakiem aktywnym
                '\n' => "\\par\n",
                '\t' => " ",
                _ when char.IsControl(ch) => "",
                _ => ch.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>Tekst w jednej linii (tytuł, nagłówek, metadane PDF): nowe linie jako spacje – \par w argumencie \textbf przerwałby kompilację.</summary>
    public static string EscapeLine(string? text) =>
        Escape(text?.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' '));

    /// <summary>Adres do <c>\url{}</c>: znaki, które łamią argument polecenia, w postaci bezpiecznej.</summary>
    public static string Url(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var sb = new StringBuilder(url.Length + 8);
        foreach (var ch in url)
        {
            sb.Append(ch switch
            {
                '\\' => "%5C",
                '{' => "%7B",
                '}' => "%7D",
                '%' => @"\%",
                '#' => @"\#",
                '^' => "%5E",
                ' ' => "%20",
                '"' => "%22",
                _ when char.IsControl(ch) => "",
                _ => ch.ToString(),
            });
        }
        return sb.ToString();
    }
}
