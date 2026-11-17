using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using ZdrowieInfo.Server.Data;

namespace ZdrowieInfo.Web.Account;

/// <summary>Endpointy potrzebne stronom kont: wylogowanie i opcje WebAuthn dla passkey.</summary>
public static class AccountEndpoints
{
    public static IEndpointConventionBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var account = endpoints.MapGroup("/Account");

        account.MapPost("/Logout", async ([FromServices] SignInManager<AppUser> signInManager, [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect("~/Account/Login");
        }).RequireAuthorization();

        account.MapPost("/PasskeyCreationOptions", async (
            HttpContext context,
            [FromServices] UserManager<AppUser> userManager,
            [FromServices] SignInManager<AppUser> signInManager,
            [FromServices] IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.NotFound();

            var userName = await userManager.GetUserNameAsync(user) ?? "użytkownik";
            var json = await signInManager.MakePasskeyCreationOptionsAsync(new()
            {
                Id = await userManager.GetUserIdAsync(user),
                Name = userName,
                DisplayName = userName,
            });
            return TypedResults.Content(json, contentType: "application/json");
        }).RequireAuthorization();

        account.MapPost("/PasskeyRequestOptions", async (
            HttpContext context,
            [FromServices] UserManager<AppUser> userManager,
            [FromServices] SignInManager<AppUser> signInManager,
            [FromServices] IAntiforgery antiforgery,
            [FromQuery] string? username) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var user = string.IsNullOrEmpty(username) ? null : await userManager.FindByNameAsync(username);
            var json = await signInManager.MakePasskeyRequestOptionsAsync(user);
            return TypedResults.Content(json, contentType: "application/json");
        }).AllowAnonymous();

        return account;
    }
}
