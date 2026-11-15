using WalletPK.Identity.Domain.Users;
using WalletPK.Identity.Infrastructure.Sms;
using WalletPK.Web.Errors;

namespace WalletPK.Identity.Api.Endpoints;

/// <summary>
/// Development-only helpers for clicking through the web app without a phone (M4.7). Not mapped in
/// any other environment, so they answer 404 there.
/// <list type="bullet">
/// <item>GET /api/identity/dev/sms?phoneNumber=   the last text messages "sent" to a number, newest first (OTPs included)</item>
/// </list>
/// </summary>
public static class DevToolsEndpoints
{
    public static IEndpointRouteBuilder MapDevToolsEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        var group = app.MapGroup("/api/identity/dev").WithTags("Development only");

        group.MapGet("/sms", (string? phoneNumber, DevSmsInbox inbox) =>
            PhoneNumber.Create(phoneNumber).ToHttpResult(phone => TypedResults.Ok(inbox.For(phone.Value))));

        return app;
    }
}
