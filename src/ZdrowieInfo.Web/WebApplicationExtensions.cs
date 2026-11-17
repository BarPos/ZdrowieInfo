using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using MudBlazor.Services;
using ZdrowieInfo.Web.Account;

namespace ZdrowieInfo.Web;

public static class WebApplicationExtensions
{
    /// <summary>Blazor Web App bez globalnej interaktywności; InteractiveServer tylko na stronach panelu.</summary>
    public static IServiceCollection AddZdrowieInfoWeb(this IServiceCollection services)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        // Polskie litery w HTML wprost, nie jako encje &#x142;.
        services.AddWebEncoders(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
        services.AddMudServices();
        services.AddCascadingAuthenticationState();
        services.AddScoped<IdentityRedirectManager>();
        services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
        return services;
    }

    public static IApplicationBuilder UseMfaEnforcement(this IApplicationBuilder app) =>
        app.UseMiddleware<MfaEnforcementMiddleware>();

    public static void MapZdrowieInfoWeb(this IEndpointRouteBuilder endpoints)
    {
        // Każda strona wymaga zalogowania, chyba że ma [AllowAnonymous] (logowanie, błędy).
        // Nie FallbackPolicy: ta obejmuje też żądania bez endpointu, a tak serwowany jest _framework/blazor.web.js.
        endpoints.MapRazorComponents<App>()
            // Własny nagłówek CSP (frame-ancestors 'none') ustawia host; bez tego Blazor dokłada drugi.
            .AddInteractiveServerRenderMode(o => o.ContentSecurityFrameAncestorsPolicy = null)
            .RequireAuthorization();
        endpoints.MapAccountEndpoints();
    }
}
