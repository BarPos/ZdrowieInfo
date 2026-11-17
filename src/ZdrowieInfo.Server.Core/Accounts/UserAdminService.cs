using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Accounts;

public sealed record UserSummary(
    string Id,
    string Login,
    UserRole Role,
    bool TwoFactorEnabled,
    int Passkeys,
    bool LockedOut,
    DateTimeOffset CreatedAt);

/// <summary>
/// Operacje kont: wiersz poleceń serwera (po loginie) i panel „Użytkownicy” (po identyfikatorze). Rejestracja w aplikacji
/// jest wyłączona. Zawsze zostaje co najmniej jeden administrator, a administrator nie usunie ani nie zdegraduje sam siebie.
/// </summary>
public sealed class UserAdminService(UserManager<AppUser> users, TimeProvider time)
{
    /// <param name="role">Domyślnie administrator – pierwsze konto zakładane poleceniem serwera musi mieć dostęp do panelu.</param>
    public async Task<IdentityResult> CreateUserAsync(string login, string password, UserRole role = UserRole.Admin)
    {
        var user = new AppUser { UserName = login.Trim(), CreatedAt = time.GetUtcNow(), Role = role };
        return await users.CreateAsync(user, password);
    }

    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct = default)
    {
        var list = await users.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(ct);
        var now = time.GetUtcNow();
        var result = new List<UserSummary>();
        foreach (var user in list)
        {
            var passkeys = await users.GetPasskeysAsync(user);
            result.Add(new UserSummary(user.Id, user.UserName ?? "", user.Role, user.TwoFactorEnabled, passkeys.Count,
                user.LockoutEnd is { } end && end > now, user.CreatedAt));
        }
        return result;
    }

    /// <summary>Awaryjnie (wiersz poleceń): wyłącza TOTP, usuwa klucz uwierzytelniający i passkey, odblokowuje konto i unieważnia sesje.</summary>
    public async Task<IdentityResult> ResetMfaAsync(string login)
    {
        var user = await users.FindByNameAsync(login);
        return user is null ? NotFound(login) : await ResetMfaAsync(user);
    }

    /// <summary>
    /// Usunięcie 2FA z panelu: jak <see cref="ResetMfaAsync(string)"/>. Weryfikacja dwuetapowa jest obowiązkowa, więc przy
    /// następnym logowaniu użytkownik skonfiguruje aplikację uwierzytelniającą od nowa (np. po zgubieniu telefonu).
    /// </summary>
    public async Task<IdentityResult> ResetMfaByIdAsync(string userId)
    {
        var user = await users.FindByIdAsync(userId);
        return user is null ? NotFound() : await ResetMfaAsync(user);
    }

    private async Task<IdentityResult> ResetMfaAsync(AppUser user)
    {
        var steps = new List<Func<Task<IdentityResult>>>
        {
            () => users.SetTwoFactorEnabledAsync(user, false),
            () => users.ResetAuthenticatorKeyAsync(user),
            () => users.SetLockoutEndDateAsync(user, null),
            () => users.ResetAccessFailedCountAsync(user),
        };
        foreach (var passkey in await users.GetPasskeysAsync(user))
        {
            var id = passkey.CredentialId;
            steps.Add(() => users.RemovePasskeyAsync(user, id));
        }
        steps.Add(() => users.UpdateSecurityStampAsync(user));
        return await RunAsync(steps);
    }

    /// <summary>Nowe hasło nadane przez administratora: odblokowuje konto, zalogowane urządzenia wylogowane w ciągu 5 minut.</summary>
    public async Task<IdentityResult> SetPasswordAsync(string userId, string password)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return NotFound();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, password); // zmienia też security stamp
        return result.Succeeded
            ? await RunAsync([() => users.SetLockoutEndDateAsync(user, null), () => users.ResetAccessFailedCountAsync(user)])
            : result;
    }

    public async Task<IdentityResult> UnlockAsync(string userId)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return NotFound();
        return await RunAsync([() => users.SetLockoutEndDateAsync(user, null), () => users.ResetAccessFailedCountAsync(user)]);
    }

    /// <summary>
    /// Zmiana roli. Odebranie uprawnień administratora unieważnia sesje (wylogowanie w ciągu 5 minut, także otwarty panel);
    /// nadanie – nowa rola w ciasteczku przy najbliższym odświeżeniu (do 5 minut) albo po ponownym zalogowaniu.
    /// </summary>
    public async Task<IdentityResult> SetRoleAsync(string userId, UserRole role, string actingUserId)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return NotFound();
        if (user.Role == role) return IdentityResult.Success;
        if (user.Role == UserRole.Admin)
        {
            if (user.Id == actingUserId) return Error("SelfDemote", "Nie możesz odebrać uprawnień administratora samemu sobie.");
            if (await IsLastAdminAsync(user)) return Error("LastAdmin", "To jedyny administrator – najpierw nadaj tę rolę komuś innemu.");
        }
        user.Role = role;
        var result = await users.UpdateAsync(user);
        if (result.Succeeded && role == UserRole.User) result = await users.UpdateSecurityStampAsync(user);
        return result;
    }

    public async Task<IdentityResult> DeleteAsync(string userId, string actingUserId)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return NotFound();
        if (user.Id == actingUserId) return Error("SelfDelete", "Nie możesz usunąć własnego konta.");
        if (user.Role == UserRole.Admin && await IsLastAdminAsync(user))
            return Error("LastAdmin", "To jedyny administrator – nie można go usunąć.");
        return await users.DeleteAsync(user); // subskrypcje push i passkey usuwane kaskadowo
    }

    /// <summary>Hasło startowe nadawane z panelu (pokazywane raz): 16 znaków, małe i wielkie litery oraz cyfry, bez mylących (0/O, 1/l/I).</summary>
    public static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        while (true)
        {
            var password = System.Security.Cryptography.RandomNumberGenerator.GetString(alphabet, 16);
            if (password.Any(char.IsLower) && password.Any(char.IsUpper) && password.Any(char.IsDigit)) return password;
        }
    }

    private async Task<bool> IsLastAdminAsync(AppUser user) =>
        !await users.Users.AnyAsync(u => u.Role == UserRole.Admin && u.Id != user.Id);

    private static async Task<IdentityResult> RunAsync(IEnumerable<Func<Task<IdentityResult>>> steps)
    {
        foreach (var step in steps)
        {
            var result = await step();
            if (!result.Succeeded) return result;
        }
        return IdentityResult.Success;
    }

    private static IdentityResult NotFound(string? login = null) =>
        Error("NotFound", login is null ? "Nie ma takiego użytkownika." : $"Nie ma użytkownika {login}.");

    private static IdentityResult Error(string code, string description) =>
        IdentityResult.Failed(new IdentityError { Code = code, Description = description });
}
