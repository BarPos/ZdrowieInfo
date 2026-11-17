using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using ZdrowieInfo.Server.Sources.Checking;
using ZdrowieInfo.Server.Sources.Fetching;
using ZdrowieInfo.Server.Sources.Scheduling;

namespace ZdrowieInfo.Server.Sources;

public static class SourcesServiceCollectionExtensions
{
    /// <summary>Źródła: pobieranie, wykrywanie zmian, incydenty, harmonogram.</summary>
    public static IServiceCollection AddSources(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SourcesOptions>().Bind(configuration.GetSection(SourcesOptions.Section));

        // Jeden kontener ciasteczek na cały czas życia procesu (np. ciasteczka Imperva), więc handler bez rotacji.
        var cookies = new CookieContainer();
        services.AddHttpClient(SourceFetcher.HttpClientName, (sp, client) =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(SourceFetcher.BuildUserAgent(sp.GetRequiredService<IOptions<SourcesOptions>>().Value));
                client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pl-PL,pl;q=0.9");
                client.Timeout = TimeSpan.FromMinutes(2);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                CookieContainer = cookies,
                UseCookies = true,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .AddResilienceHandler("sources", (pipeline, context) =>
            {
                var o = context.ServiceProvider.GetRequiredService<IOptions<SourcesOptions>>().Value;
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.FromSeconds(3),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                });
                pipeline.AddTimeout(o.RequestTimeout);
            });

        services.AddSingleton<HostThrottle>();
        services.AddSingleton<SourceFetcher>();
        services.AddSingleton<SourceCheckService>();
        services.AddSingleton<SourceScheduler>();
        services.AddSingleton<SourceSeeder>();
        services.AddSingleton<SourceAdminService>();
        services.AddHostedService<SchedulerService>();
        return services;
    }
}
