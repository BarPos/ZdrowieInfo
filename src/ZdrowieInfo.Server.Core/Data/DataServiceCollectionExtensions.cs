using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ZdrowieInfo.Server.Data;

public static class DataServiceCollectionExtensions
{
    public const string ConnectionStringName = "ZdrowieInfo";

    /// <summary>PostgreSQL przez EF Core; nazwy tabel i kolumn w snake_case. Fabryka dla usług tła, scoped context dla stron.</summary>
    public static IServiceCollection AddZdrowieInfoData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Brak ConnectionStrings:{ConnectionStringName} w konfiguracji.");

        services.AddDbContextFactory<ZdrowieInfoDbContext>(o => o
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly("ZdrowieInfo.Server"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ZdrowieInfoDbContext>>().CreateDbContext());
        return services;
    }
}
