using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ZdrowieInfo.Server.Accounts;
using ZdrowieInfo.Server.Sources.Fetching;

namespace ZdrowieInfo.Server.Tests.Infrastructure;

/// <summary>Aplikacja w pamięci na własnej bazie i własnym katalogu danych (klucze Data Protection).</summary>
public sealed class AppFactory(string connectionString, string dataDirectory, FakeTimeProvider? time, Action<IServiceCollection>? services) : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = dataDirectory;

    /// <summary>Wszystkie żądania do źródeł idą tutaj – nigdy do internetu.</summary>
    public FakeHttp Http { get; } = new();

    /// <summary>Sztuczny zegar, jeśli test go podał (inaczej czas systemowy).</summary>
    public FakeTimeProvider? Time { get; } = time;

    private readonly Action<IServiceCollection>? configureServices = services;

    public static async Task<AppFactory> CreateAsync(PostgresFixture postgres, string? dataDirectory = null, string? connectionString = null,
        FakeTimeProvider? time = null, Action<IServiceCollection>? services = null)
    {
        var dir = dataDirectory ?? Directory.CreateTempSubdirectory("zdrowieinfo-test-").FullName;
        var factory = new AppFactory(connectionString ?? await postgres.CreateDatabaseAsync(), dir, time, services);
        _ = factory.Server; // start + migracje + lista źródeł
        return factory;
    }

    public string ConnectionString { get; } = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseStaticWebAssets(); // jak w Development: zasoby z pakietów (blazor.web.js) z manifestu, nie z wwwroot
        builder.UseSetting("ConnectionStrings:ZdrowieInfo", ConnectionString);
        builder.UseSetting("ZdrowieInfo:PublicUrl", "https://localhost");
        builder.UseSetting("ZdrowieInfo:DataDirectory", DataDirectory);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("ZdrowieInfo:Sources:SchedulerEnabled", "false");
        builder.UseSetting("ZdrowieInfo:Sources:MinDelayPerHost", "00:00:00");
        builder.UseSetting("ZdrowieInfo:Sources:MaxDelayPerHost", "00:00:00");
        builder.UseSetting("ZdrowieInfo:Reports:Generator", "Sample");
        builder.UseSetting("ZdrowieInfo:Export:Enabled", "false"); // kompilacja LaTeX tylko w testach eksportu

        builder.ConfigureTestServices(services =>
        {
            if (Time is not null) services.AddSingleton<TimeProvider>(Time);
            services.AddHttpClient(SourceFetcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Http);
            configureServices?.Invoke(services);
        });
    }

    /// <summary>Klient HTTPS (ciasteczka mają Secure), bez automatycznych przekierowań, z ciasteczkami.</summary>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    public async Task CreateUserAsync(string login, string password)
    {
        await using var scope = Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<UserAdminService>().CreateUserAsync(login, password);
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}
