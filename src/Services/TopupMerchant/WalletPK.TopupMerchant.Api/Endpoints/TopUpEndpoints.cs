using Microsoft.AspNetCore.Mvc;
using WalletPK.Common.Correlation;
using WalletPK.Common.Results;
using WalletPK.TopupMerchant.Application.TopUps;
using WalletPK.Web.Authentication;
using WalletPK.Web.Errors;
using WalletPK.Web.Idempotency;
using WalletPK.Web.Identity;

namespace WalletPK.TopupMerchant.Api.Endpoints;

/// <param name="Msisdn">The number to top up ("0300 1234567").</param>
/// <param name="AmountPaisa">Airtime in whole rupees, or the bundle's price.</param>
/// <param name="BundleCode">A bundle from /api/topups/networks/{code}/bundles (none: airtime).</param>
/// <param name="BundleName">The bundle's name as shown (for the receipt).</param>
public sealed record TopUpRequest(string? Msisdn, long? AmountPaisa, string? BundleCode, string? BundleName);

/// <param name="Reason">Why the money is returned (kept on the timeline).</param>
public sealed record TopUpRefundRequest(string? Reason);

/// <param name="TelcoReference">The network's reference for the top-up, from its settlement file.</param>
public sealed record TopUpConfirmRequest(string? TelcoReference);

/// <summary>
/// Mobile top-ups for the signed-in customer (M8.1, access token required):
/// <list type="bullet">
/// <item>GET  /api/topups/networks                  the networks and their availability (circuit breakers)</item>
/// <item>GET  /api/topups/detect?msisdn=            which network a number is on</item>
/// <item>GET  /api/topups/networks/{code}/bundles   a network's bundles</item>
/// <item>POST /api/topups                           top up (Idempotency-Key): 202, follow it at /api/topups/{id}</item>
/// <item>GET  /api/topups[/{id}]                    my top-ups / one with its timeline</item>
/// </list>
/// </summary>
public static class TopUpEndpoints
{
    public static IEndpointRouteBuilder MapTopUpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/topups").WithTags("Top-ups").RequireAuthorization();

        group.MapGet("/networks", (TopUpService topUps) => TypedResults.Ok(topUps.Networks()));

        group.MapGet("/detect", (string? msisdn, TopUpService topUps) =>
            topUps.Detect(msisdn).ToHttpResult(network => TypedResults.Ok(new { network.Code, network.Name })));

        group.MapGet("/networks/{code}/bundles", async (string code, TopUpService topUps, CancellationToken cancellationToken) =>
            (await topUps.BundlesAsync(code, cancellationToken)).ToHttpResult());

        group.MapPost("/", async (
            TopUpRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string? idempotencyKey,
            ICurrentUser currentUser,
            TopUpService topUps,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            {
                return Error.Validation("idempotency.key_required", $"Send a unique {IdempotencyMiddleware.HeaderName} header (at most 100 characters) with every top-up.").ToProblem();
            }

            var result = await topUps.TopUpAsync(
                currentUser.GetRequiredId().Value, currentUser.DeviceId, idempotencyKey, request.Msisdn, request.AmountPaisa ?? 0, request.BundleCode, request.BundleName,
                CorrelationContext.Current, cancellationToken);
            return result.ToHttpResult(sent => sent.Created
                ? TypedResults.Accepted($"/api/topups/{sent.TopUp.Id}", sent.TopUp)
                : TypedResults.Ok(sent.TopUp));
        });

        group.MapGet("/{id:guid}", async (Guid id, ICurrentUser currentUser, TopUpService topUps, CancellationToken cancellationToken) =>
            (await topUps.GetAsync(currentUser.GetRequiredId().Value, id, cancellationToken)).ToHttpResult());

        group.MapGet("/", async (int? limit, ICurrentUser currentUser, TopUpService topUps, CancellationToken cancellationToken) =>
            TypedResults.Ok(await topUps.ListAsync(currentUser.GetRequiredId().Value, limit, cancellationToken)));

        return endpoints;
    }

    /// <summary>
    /// The top-up dead-letter queue (FR-4.3), for the operations roles:
    /// <list type="bullet">
    /// <item>GET  /api/topups/ops/dead-letters        dead-lettered top-ups, oldest first, with what can be done</item>
    /// <item>POST /api/topups/ops/{id}/replay         try the step that was given up on again</item>
    /// <item>POST /api/topups/ops/{id}/refund         never applied: return the money</item>
    /// <item>POST /api/topups/ops/{id}/confirm        applied (seen in the settlement file): settle the money</item>
    /// </list>
    /// </summary>
    public static IEndpointRouteBuilder MapTopUpOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var ops = endpoints.MapGroup("/api/topups/ops").WithTags("Top-up operations").RequireAuthorization(WalletPkPolicies.Operations);

        ops.MapGet("/dead-letters", async (int? limit, TopUpService topUps, CancellationToken cancellationToken) =>
            TypedResults.Ok(await topUps.DeadLettersAsync(limit, cancellationToken)));

        ops.MapPost("/{id:guid}/replay", async (Guid id, ICurrentUser currentUser, TopUpService topUps, CancellationToken cancellationToken) =>
            (await topUps.ReplayAsync(id, OperatorOf(currentUser), cancellationToken)).ToHttpResult());

        ops.MapPost("/{id:guid}/refund", async (Guid id, TopUpRefundRequest? request, ICurrentUser currentUser, TopUpService topUps, CancellationToken cancellationToken) =>
            (await topUps.RefundAsync(id, OperatorOf(currentUser), request?.Reason, cancellationToken)).ToHttpResult());

        ops.MapPost("/{id:guid}/confirm", async (Guid id, TopUpConfirmRequest request, ICurrentUser currentUser, TopUpService topUps, CancellationToken cancellationToken) =>
            (await topUps.ConfirmAsync(id, OperatorOf(currentUser), request.TelcoReference, cancellationToken)).ToHttpResult());

        return endpoints;
    }

    private static string OperatorOf(ICurrentUser user) => $"operator {user.GetRequiredId().Value}";
}
