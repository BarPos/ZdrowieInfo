using Microsoft.AspNetCore.Identity;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Accounts;

/// <summary>Operacje kont wywoływane z wiersza poleceń serwera (rejestracja w aplikacji jest wyłączona).</summary>
public sealed class UserAdminService(UserManager<AppUser> users, TimeProvider time)
{
    public async Task<IdentityResult> CreateUserAsync(string login, string password)
    {
        var user = new AppUser { UserName = login, CreatedAt = time.GetUtcNow() };
        return await users.CreateAsync(user, password);
    }

    /// <summary>Awaryjnie: wyłącza TOTP, usuwa klucz uwierzytelniający i passkey, odblokowuje konto i unieważnia sesje.</summary>
    public async Task<IdentityResult> ResetMfaAsync(string login)
    {
        var user = await users.FindByNameAsync(login);
        if (user is null)
            return IdentityResult.Failed(new IdentityError { Code = "NotFound", Description = $"Nie ma użytkownika {login}." });

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

        foreach (var step in steps)
        {
            var result = await step();
            if (!result.Succeeded) return result;
        }
        return IdentityResult.Success;
    }
}
