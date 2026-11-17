using Microsoft.AspNetCore.Identity;

namespace ZdrowieInfo.Server.Data;

/// <summary>Użytkownik aplikacji. Logowanie po nazwie użytkownika (bez e-maila).</summary>
public sealed class AppUser : IdentityUser
{
    public DateTimeOffset CreatedAt { get; set; }
}
