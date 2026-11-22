using WalletPK.Identity.Application.Account;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Identity.Api.Endpoints;

/// <param name="Code">The six-digit code from the SMS.</param>
public sealed record StepUpCodeRequest(string? Code);

/// <param name="Mpin">The user's five-digit MPIN.</param>
/// <param name="IdempotencyKey">The Idempotency-Key the payment will be sent with (for paying a money request: "money-request:{id}").</param>
public sealed record ConfirmMpinRequest(string? Mpin, string? IdempotencyKey);

/// <summary>
/// The signed-in user's own account; every endpoint needs a valid access token:
/// <list type="bullet">
/// <item>GET    /api/identity/me                   profile + current device</item>
/// <item>GET    /api/identity/devices              all devices</item>
/// <item>DELETE /api/identity/devices/{deviceId}   remove a device and end its sessions</item>
/// <item>POST   /api/identity/step-up              send a code to confirm a payment the fraud engine stepped up</item>
/// <item>POST   /api/identity/step-up/verify       check it; the payment can then be sent again</item>
/// <item>POST   /api/identity/mpin/confirm         MPIN for one payment: a token to send as X-Mpin-Confirmation</item>
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

        group.MapPost("/step-up", async (ICurrentUser currentUser, PaymentStepUpHandler handler, CancellationToken cancellationToken) =>
            (await handler.StartAsync(currentUser.GetRequiredId(), cancellationToken)).ToHttpResult());

        group.MapPost("/step-up/verify", async (StepUpCodeRequest request, ICurrentUser currentUser, PaymentStepUpHandler handler, CancellationToken cancellationToken) =>
            (await handler.VerifyAsync(currentUser.GetRequiredId(), CurrentDevice(currentUser)?.Value, request.Code, cancellationToken)).ToHttpResult());

        group.MapPost("/mpin/confirm", async (ConfirmMpinRequest request, ICurrentUser currentUser, ConfirmMpinHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(currentUser.GetRequiredId(), CurrentDevice(currentUser), request.Mpin, request.IdempotencyKey, cancellationToken)).ToHttpResult());

        return endpoints;
    }

    private static DeviceId? CurrentDevice(ICurrentUser currentUser) =>
        DeviceId.TryParse(currentUser.DeviceId, null, out var deviceId) ? deviceId : null;
}
