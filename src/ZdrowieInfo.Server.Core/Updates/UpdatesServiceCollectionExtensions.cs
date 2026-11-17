using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ZdrowieInfo.Server.Updates;

public static class UpdatesServiceCollectionExtensions
{
    /// <summary>Aktualizacja serwera z wydań GitHub (panel → Aktualizacje) przez update.sh.</summary>
    public static IServiceCollection AddUpdates(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<UpdatesOptions>().Bind(configuration.GetSection(UpdatesOptions.Section));
        services.AddHttpClient(GitHubReleaseSource.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(10));
        services.AddSingleton<IReleaseSource, GitHubReleaseSource>();
        services.AddSingleton<IServerInstallation, ServerInstallation>();
        services.AddSingleton<ServerUpdateService>();
        services.AddHostedService<UpdatesBackgroundService>();
        return services;
    }
}
