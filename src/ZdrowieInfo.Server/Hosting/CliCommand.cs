namespace ZdrowieInfo.Server.Hosting;

internal enum CliCommandKind { Run, MigrateOnly, CreateUser, SetPassword, ResetMfa, Invalid }

internal sealed record CliCommand(CliCommandKind Kind, string? Login = null, string? Error = null)
{
    public const string Usage = """
        Użycie:
          ZdrowieInfo.Server                         uruchamia aplikację
          ZdrowieInfo.Server --migrate-only          stosuje migracje bazy i kończy
          ZdrowieInfo.Server --create-user <login>   tworzy konto (hasło ze standardowego wejścia)
          ZdrowieInfo.Server --set-password <login>  ustawia nowe hasło i unieważnia sesje
          ZdrowieInfo.Server --reset-mfa <login>     awaryjnie wyłącza TOTP i passkey, odblokowuje konto
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
