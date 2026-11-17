using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(ZdrowieInfo.Server.Tests.Infrastructure.PostgresFixture))]

namespace ZdrowieInfo.Server.Tests.Infrastructure;

/// <summary>Jeden kontener PostgreSQL na cały przebieg testów; każda fabryka aplikacji dostaje własną bazę.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Klasy testów działają równolegle, każda aplikacja ma własną pulę połączeń – domyślne 100 to za mało.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithCommand("-c", "max_connections=500")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "zi_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}
