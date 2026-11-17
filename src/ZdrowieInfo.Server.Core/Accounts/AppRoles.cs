using System.Security.Claims;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Server.Accounts;

/// <summary>Role w claimie <see cref="ClaimTypes.Role"/> (z kolumny <c>role</c> użytkownika) i polityka administratora.</summary>
public static class AppRoles
{
    public const string Admin = "admin";
    public const string User = "uzytkownik";

    /// <summary>Polityka stron i akcji tylko dla administratora (panel, incydenty, PDF, udostępnianie, użytkownicy).</summary>
    public const string AdminPolicy = "admin";

    public static string Name(UserRole role) => role == UserRole.Admin ? Admin : User;

    public static string Label(UserRole role) => role == UserRole.Admin ? "administrator" : "użytkownik";

    public static bool TryParse(string? value, out UserRole role)
    {
        role = value?.Trim().ToLowerInvariant() switch
        {
            Admin or "administrator" => UserRole.Admin,
            User or "użytkownik" or "user" => UserRole.User,
            _ => (UserRole)(-1),
        };
        return role is UserRole.Admin or UserRole.User;
    }

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Admin);
}
