using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Accounts;

/// <summary>Dodaje claim <see cref="MfaClaims.Enabled"/>; odświeżany przy logowaniu, RefreshSignInAsync i walidacji security stamp.</summary>
public sealed class AppUserClaimsPrincipalFactory(UserManager<AppUser> userManager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser>(userManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (await UserManager.GetTwoFactorEnabledAsync(user))
            identity.AddClaim(new Claim(MfaClaims.Enabled, "1"));
        return identity;
    }
}
