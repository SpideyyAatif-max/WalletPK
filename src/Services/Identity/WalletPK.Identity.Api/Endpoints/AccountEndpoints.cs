using WalletPK.Identity.Application.Account;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Identity.Api.Endpoints;

/// <summary>
/// The signed-in user's own account; every endpoint needs a valid access token:
/// <list type="bullet">
/// <item>GET    /api/identity/me                   profile + current device</item>
/// <item>GET    /api/identity/devices              all devices</item>
/// <item>DELETE /api/identity/devices/{deviceId}   remove a device and end its sessions</item>
/// </list>
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/identity").WithTags("Account").RequireAuthorization();

        group.MapGet("/me", async (ICurrentUser currentUser, GetProfileHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(currentUser.GetRequiredId(), CurrentDevice(currentUser), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapGet("/devices", async (ICurrentUser currentUser, ListDevicesHandler handler, CancellationToken cancellationToken) =>
            TypedResults.Ok(await handler.HandleAsync(currentUser.GetRequiredId(), CurrentDevice(currentUser), cancellationToken)));

        group.MapDelete("/devices/{deviceId:guid}", async (Guid deviceId, ICurrentUser currentUser, RevokeDeviceHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(currentUser.GetRequiredId(), new DeviceId(deviceId), cancellationToken);
            return result.ToHttpResult();
        });

        return endpoints;
    }

    private static DeviceId? CurrentDevice(ICurrentUser currentUser) =>
        DeviceId.TryParse(currentUser.DeviceId, null, out var deviceId) ? deviceId : null;
}
