using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ZdrowieInfo.Server.Reports;

public static class ReportsServiceCollectionExtensions
{
    /// <summary>Raporty dzienne: generator (Claude albo przykładowy), walidacja, harmonogram.</summary>
    public static IServiceCollection AddReports(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ReportsOptions>().Bind(configuration.GetSection(ReportsOptions.Section));
        services.AddSingleton<ReportSettings>();
        services.AddSingleton<ClaudeReportGenerator>();
        services.AddSingleton<SampleReportGenerator>();
        services.AddSingleton<IReportGenerator>(sp =>
            string.Equals(sp.GetRequiredService<IOptions<ReportsOptions>>().Value.Generator, "Sample", StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<SampleReportGenerator>()
                : sp.GetRequiredService<ClaudeReportGenerator>());
        services.AddSingleton<DailyReportService>();
        services.AddSingleton<ReportScheduler>();
        services.AddSingleton<Search.ReportSearch>();
        return services;
    }
}
