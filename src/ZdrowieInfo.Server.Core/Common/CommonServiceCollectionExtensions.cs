using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using ZdrowieInfo.Server.Settings;

namespace ZdrowieInfo.Server.Common;

public static class CommonServiceCollectionExtensions
{
    /// <summary>Czas, strefa Warszawy, Data Protection z kluczami na dysku (bez tego restart wylogowuje) i app_settings.</summary>
    public static IServiceCollection AddZdrowieInfoCommon(this IServiceCollection services, ZdrowieInfoOptions app)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<WarsawClock>();

        Directory.CreateDirectory(app.ResolvedKeysDirectory);
        services.AddDataProtection()
            .SetApplicationName("ZdrowieInfo")
            .PersistKeysToFileSystem(new DirectoryInfo(app.ResolvedKeysDirectory));

        services.AddSingleton<AppSettingsStore>();
        return services;
    }
}
