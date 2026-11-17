using System.ComponentModel.DataAnnotations;
using ZdrowieInfo.Server.Common;

namespace ZdrowieInfo.Server.Hosting;

internal static class OptionsValidation
{
    /// <summary>Błędy konfiguracji wychodzą przed zbudowaniem usług – inaczej np. zły PublicUrl wywróciłby rejestrację passkey.</summary>
    public static void ThrowIfInvalid(ZdrowieInfoOptions options)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true))
            throw new InvalidOperationException("Nieprawidłowa konfiguracja ZdrowieInfo: " + string.Join(" ", results.Select(r => r.ErrorMessage)));
    }
}
