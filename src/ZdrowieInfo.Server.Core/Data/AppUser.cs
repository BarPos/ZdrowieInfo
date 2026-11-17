using Microsoft.AspNetCore.Identity;

namespace ZdrowieInfo.Server.Data;

public enum UserRole
{
    /// <summary>Czyta i przeszukuje raporty, zarządza swoim kontem.</summary>
    User = 0,
    /// <summary>Wszystko: panel, źródła, incydenty, PDF, udostępnianie, użytkownicy.</summary>
    Admin = 1,
}

/// <summary>Użytkownik aplikacji. Logowanie po nazwie użytkownika (bez e-maila).</summary>
public sealed class AppUser : IdentityUser
{
    public DateTimeOffset CreatedAt { get; set; }
    public UserRole Role { get; set; }
}
