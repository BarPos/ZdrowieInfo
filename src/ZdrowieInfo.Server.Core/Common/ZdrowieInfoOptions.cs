using System.ComponentModel.DataAnnotations;

namespace ZdrowieInfo.Server.Common;

/// <summary>Sekcja konfiguracji <c>ZdrowieInfo</c>.</summary>
public sealed class ZdrowieInfoOptions : IValidatableObject
{
    public const string Section = "ZdrowieInfo";

    /// <summary>Publiczny adres aplikacji, np. https://zdrowieinfo.example.pl. Z niego wynikają linki, VAPID i RP ID passkey.</summary>
    [Required]
    public string PublicUrl { get; set; } = "";

    /// <summary>Katalog danych (raporty, pliki tymczasowe).</summary>
    [Required]
    public string DataDirectory { get; set; } = "";

    /// <summary>Katalog kluczy Data Protection. Domyślnie &lt;DataDirectory&gt;/keys.</summary>
    public string? KeysDirectory { get; set; }

    /// <summary>Adresy IP reverse proxy, któremu ufamy w nagłówkach X-Forwarded-*. Domyślnie tylko loopback.</summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>Czy wymagać adresu https:// w PublicUrl (wyłączane tylko w środowisku deweloperskim i testach).</summary>
    public bool RequireHttpsPublicUrl { get; set; } = true;

    public Uri PublicUri => new(PublicUrl, UriKind.Absolute);

    public string ResolvedKeysDirectory => string.IsNullOrWhiteSpace(KeysDirectory)
        ? Path.Combine(DataDirectory, "keys")
        : KeysDirectory;

    /// <summary>Ścieżki względne (np. w appsettings.Development.json) liczone od katalogu aplikacji, nie od bieżącego katalogu.</summary>
    public void ResolvePaths(string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory) && !Path.IsPathFullyQualified(DataDirectory))
            DataDirectory = Path.GetFullPath(Path.Combine(contentRoot, DataDirectory));
        if (!string.IsNullOrWhiteSpace(KeysDirectory) && !Path.IsPathFullyQualified(KeysDirectory))
            KeysDirectory = Path.GetFullPath(Path.Combine(contentRoot, KeysDirectory));
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri))
        {
            yield return new ValidationResult("ZdrowieInfo:PublicUrl musi być pełnym adresem, np. https://zdrowieinfo.example.pl.", [nameof(PublicUrl)]);
            yield break;
        }
        if (RequireHttpsPublicUrl && uri.Scheme != Uri.UriSchemeHttps)
            yield return new ValidationResult("ZdrowieInfo:PublicUrl musi zaczynać się od https:// (Web Push, passkey i PWA wymagają HTTPS).", [nameof(PublicUrl)]);
        if (uri.AbsolutePath != "/")
            yield return new ValidationResult("ZdrowieInfo:PublicUrl nie może zawierać ścieżki – aplikacja działa w katalogu głównym domeny.", [nameof(PublicUrl)]);
    }
}
