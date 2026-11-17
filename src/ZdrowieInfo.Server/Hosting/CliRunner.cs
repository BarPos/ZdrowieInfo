using System.Text;
using Microsoft.AspNetCore.Identity;
using ZdrowieInfo.Server.Accounts;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Sources;

namespace ZdrowieInfo.Server.Hosting;

internal static class CliRunner
{
    public static async Task<int> RunAsync(CliCommand command, IServiceProvider services)
    {
        await DatabaseMigrator.MigrateAsync(services);
        await services.GetRequiredService<SourceSeeder>().SeedAsync();
        if (command.Kind == CliCommandKind.MigrateOnly)
        {
            Console.WriteLine("Baza danych jest aktualna.");
            return 0;
        }
        if (command.Kind == CliCommandKind.CheckSources) return await SourceCli.TrySelectorsAsync(services, command.Login);
        if (command.Kind == CliCommandKind.CheckNow) return await SourceCli.CheckNowAsync(services, command.Login);
        if (command.Kind == CliCommandKind.ApplyDefaultSources) return await SourceCli.ApplyDefaultsAsync(services, command.Login);
        if (command.Kind == CliCommandKind.GenerateReport) return await ReportCli.GenerateAsync(services, command.Login, command.Replace);
        if (command.Kind == CliCommandKind.ExportReport) return await ReportCli.ExportAsync(services, command.Login);
        if (command.Kind == CliCommandKind.MarkReportedBefore) return await ReportCli.MarkReportedBeforeAsync(services, DateOnly.ParseExact(command.Login!, "yyyy-MM-dd"));

        await using var scope = services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdminService>();
        var login = command.Login!;

        IdentityResult result;
        switch (command.Kind)
        {
            case CliCommandKind.CreateUser:
                ZdrowieInfo.Server.Accounts.AppRoles.TryParse(command.Role ?? ZdrowieInfo.Server.Accounts.AppRoles.Admin, out var role);
                result = await admin.CreateUserAsync(login, ReadNewPassword(), role);
                break;
            case CliCommandKind.SetPassword:
                result = await SetPasswordAsync(scope.ServiceProvider, login, ReadNewPassword());
                break;
            case CliCommandKind.ResetMfa:
                result = await admin.ResetMfaAsync(login);
                break;
            default:
                throw new InvalidOperationException($"Nieobsługiwane polecenie {command.Kind}.");
        }

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) Console.Error.WriteLine(error.Description);
            return 1;
        }

        Console.WriteLine(command.Kind switch
        {
            CliCommandKind.CreateUser => $"Utworzono konto {login} ({(command.Role is null ? "administrator" : command.Role)}). Przy pierwszym logowaniu aplikacja poprosi o konfigurację aplikacji uwierzytelniającej.",
            CliCommandKind.SetPassword => $"Ustawiono nowe hasło dla {login}. Zalogowane urządzenia zostaną wylogowane w ciągu 5 minut.",
            _ => $"Wyłączono TOTP i passkey dla {login}; zalogowane urządzenia zostaną wylogowane w ciągu 5 minut. Przy następnym logowaniu skonfiguruj aplikację uwierzytelniającą ponownie.",
        });
        return 0;
    }

    private static async Task<IdentityResult> SetPasswordAsync(IServiceProvider sp, string login, string password)
    {
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByNameAsync(login);
        if (user is null)
            return IdentityResult.Failed(new IdentityError { Code = "NotFound", Description = $"Nie ma użytkownika {login}." });

        var token = await users.GeneratePasswordResetTokenAsync(user); // zmienia też security stamp
        var result = await users.ResetPasswordAsync(user, token, password);
        if (result.Succeeded) await users.SetLockoutEndDateAsync(user, null);
        return result;
    }

    /// <summary>Hasło z terminala (bez echa, dwa razy) albo jedna linia ze standardowego wejścia (skrypty, testy).</summary>
    private static string ReadNewPassword()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? throw new InvalidOperationException("Brak hasła na standardowym wejściu.");

        var first = ReadHidden("Nowe hasło (min. 12 znaków): ");
        var second = ReadHidden("Powtórz hasło: ");
        if (first != second) throw new InvalidOperationException("Hasła nie są takie same.");
        return first;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
