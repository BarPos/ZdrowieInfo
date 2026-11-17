namespace ZdrowieInfo.Server.Hosting;

internal enum CliCommandKind { Run, MigrateOnly, CreateUser, SetPassword, ResetMfa, CheckSources, CheckNow, ApplyDefaultSources, GenerateReport, MarkReportedBefore, Invalid }

internal sealed record CliCommand(CliCommandKind Kind, string? Login = null, string? Error = null, bool Replace = false)
{
    public const string Usage = """
        Użycie:
          ZdrowieInfo.Server                         uruchamia aplikację
          ZdrowieInfo.Server --migrate-only          stosuje migracje bazy i kończy
          ZdrowieInfo.Server --create-user <login>   tworzy konto (hasło ze standardowego wejścia)
          ZdrowieInfo.Server --set-password <login>  ustawia nowe hasło i unieważnia sesje
          ZdrowieInfo.Server --reset-mfa <login>     awaryjnie wyłącza TOTP i passkey, odblokowuje konto
          ZdrowieInfo.Server --check-sources [slug]  próba selektorów na żywo, bez zapisu (wszystkie albo jedno źródło)
          ZdrowieInfo.Server --check-now [slug]      sprawdza źródła teraz i zapisuje wynik (jak harmonogram)
          ZdrowieInfo.Server --apply-default-sources [slug]  przywraca adresy i selektory z listy domyślnej (nowy stan bazowy)
          ZdrowieInfo.Server --generate-report [rrrr-mm-dd] [--replace]  przygotowuje raport dnia (domyślnie dziś)
          ZdrowieInfo.Server --mark-reported-before rrrr-mm-dd  oznacza wcześniejsze wpisy jako zaraportowane (start raportów)
        """;

    private static readonly Dictionary<string, CliCommandKind> WithLogin = new(StringComparer.Ordinal)
    {
        ["--create-user"] = CliCommandKind.CreateUser,
        ["--set-password"] = CliCommandKind.SetPassword,
        ["--reset-mfa"] = CliCommandKind.ResetMfa,
    };

    /// <summary>Rozpoznaje polecenie; pozostałe argumenty (np. --urls, --environment) zostają dla hosta.</summary>
    public static CliCommand Parse(string[] args)
    {
        CliCommand? found = null;
        for (var i = 0; i < args.Length; i++)
        {
            CliCommand? current = null;
            if (args[i] == "--migrate-only")
            {
                current = new CliCommand(CliCommandKind.MigrateOnly);
            }
            else if (args[i] is "--check-sources" or "--check-now" or "--apply-default-sources")
            {
                var kindOptional = args[i] switch
                {
                    "--check-sources" => CliCommandKind.CheckSources,
                    "--check-now" => CliCommandKind.CheckNow,
                    _ => CliCommandKind.ApplyDefaultSources,
                };
                var slug = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
                current = new CliCommand(kindOptional, slug);
            }
            else if (args[i] == "--generate-report")
            {
                var date = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
                current = new CliCommand(CliCommandKind.GenerateReport, date, Replace: args.Contains("--replace"));
            }
            else if (args[i] == "--mark-reported-before")
            {
                if (i + 1 >= args.Length || !DateOnly.TryParseExact(args[i + 1], "yyyy-MM-dd", out _))
                    return new CliCommand(CliCommandKind.Invalid, Error: "--mark-reported-before wymaga daty rrrr-mm-dd.");
                current = new CliCommand(CliCommandKind.MarkReportedBefore, args[++i]);
            }
            else if (args[i] == "--replace")
            {
                continue;
            }
            else if (WithLogin.TryGetValue(args[i], out var kind))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return new CliCommand(CliCommandKind.Invalid, Error: $"{args[i]} wymaga loginu.");
                current = new CliCommand(kind, args[++i]);
            }

            if (current is null) continue;
            if (found is not null) return new CliCommand(CliCommandKind.Invalid, Error: "Podaj tylko jedno polecenie.");
            found = current;
        }
        return found ?? new CliCommand(CliCommandKind.Run);
    }
}
