using System.Text.Json;

namespace ZdrowieInfo.Server.Data;

/// <summary>Wiersz tabeli <c>app_settings</c>: klucz i wartość JSON; wartości wrażliwe zaszyfrowane Data Protection.</summary>
public sealed class AppSetting
{
    public required string Key { get; set; }

    /// <summary>Wartość JSON. Dla wartości chronionych: JSON string z szyfrogramem.</summary>
    public required JsonDocument Value { get; set; }

    public bool IsProtected { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
