using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ZdrowieInfo.Server.Export;

public static class ExportServiceCollectionExtensions
{
    public static IServiceCollection AddReportExport(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ExportOptions>().Bind(configuration.GetSection(ExportOptions.Section));
        services.AddSingleton<LatexReportRenderer>();
        services.AddSingleton<PdfCompiler>();
        services.AddSingleton<ReportExporter>();
        return services;
    }
}
