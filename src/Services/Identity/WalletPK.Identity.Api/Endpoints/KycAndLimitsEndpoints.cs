using WalletPK.Identity.Application.Kyc;
using WalletPK.Identity.Application.Limits;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Identity.Api.Endpoints;

/// <summary>
/// KYC and limits for the signed-in user (access token required):
/// <list type="bullet">
/// <item>GET  /api/identity/kyc      current tier and verified details (CNIC masked)</item>
/// <item>POST /api/identity/kyc      CNIC + name + date of birth → NADRA check → L1</item>
/// <item>GET  /api/identity/limits   per-transaction, daily and monthly limits with what is used</item>
/// </list>
/// </summary>
public static class KycAndLimitsEndpoints
{
    public static IEndpointRouteBuilder MapKycAndLimitsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/identity").WithTags("KYC and limits").RequireAuthorization();

        group.MapGet("/kyc", async (ICurrentUser currentUser, GetKycStatusHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(currentUser.GetRequiredId(), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/kyc", async (SubmitKycRequest request, ICurrentUser currentUser, SubmitKycHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(
                currentUser.GetRequiredId(),
                new SubmitKycCommand(request.Cnic, request.FullName, request.DateOfBirth),
                cancellationToken);
            return result.ToHttpResult();
        });

        group.MapGet("/limits", async (ICurrentUser currentUser, LimitService limits, CancellationToken cancellationToken) =>
        {
            DeviceId? deviceId = DeviceId.TryParse(currentUser.DeviceId, null, out var id) ? id : null;
            var result = await limits.GetSummaryAsync(currentUser.GetRequiredId(), deviceId, cancellationToken);
            return result.ToHttpResult();
        });

        return endpoints;
    }
}

/// <param name="Cnic">13 digits, with or without dashes: "35202-1234567-1".</param>
/// <param name="FullName">Name exactly as on the CNIC.</param>
/// <param name="DateOfBirth">ISO date, for example "1998-04-21".</param>
public sealed record SubmitKycRequest(string? Cnic, string? FullName, DateOnly? DateOfBirth);
