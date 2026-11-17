using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ZdrowieInfo.Server.ApiKeys;
using ZdrowieInfo.Server.Common;

namespace ZdrowieInfo.Server.Mcp;

public static class McpServiceCollectionExtensions
{
    public const string Path = "/mcp";

    /// <summary>Serwer MCP (Streamable HTTP, bezstanowy) z narzędziami tylko do odczytu.</summary>
    public static IServiceCollection AddZdrowieInfoMcp(this IServiceCollection services)
    {
        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "zdrowieinfo", Title = "ZdrowieInfo", Version = AppVersion.Current };
                o.ServerInstructions =
                    "Raporty ZdrowieInfo: codzienne zmiany na stronach NFZ, CeZ, MZ i CSIRT CeZ istotne dla przychodni POZ. " +
                    "search_reports – wyszukiwanie w punktach raportów; get_report – cały raport dnia; list_sources – stan monitorowanych stron. " +
                    "Odpowiadając, podawaj adresy źródeł (sources[].url, citations[].url).";
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<ReportTools>();
        return services;
    }

    /// <summary>Wymaga klucza API z zakresem <c>mcp:read</c> (Authorization: Bearer mon_…), limit jak w API.</summary>
    public static IEndpointConventionBuilder MapZdrowieInfoMcp(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp(Path)
            .RequireAuthorization(ApiScopes.McpRead)
            .RequireRateLimiting(ApiKeysServiceCollectionExtensions.RateLimitPolicy);

}
