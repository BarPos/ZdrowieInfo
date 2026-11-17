using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ZdrowieInfo.Server.Accounts;

namespace ZdrowieInfo.Server.Tests.Infrastructure;

/// <summary>Aplikacja w pamięci na własnej bazie i własnym katalogu danych (klucze Data Protection).</summary>
public sealed class AppFactory(string connectionString, string dataDirectory) : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = dataDirectory;

    public static async Task<AppFactory> CreateAsync(PostgresFixture postgres, string? dataDirectory = null, string? connectionString = null)
    {
        var dir = dataDirectory ?? Directory.CreateTempSubdirectory("zdrowieinfo-test-").FullName;
        var factory = new AppFactory(connectionString ?? await postgres.CreateDatabaseAsync(), dir);
        _ = factory.Server; // start + migracje
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
