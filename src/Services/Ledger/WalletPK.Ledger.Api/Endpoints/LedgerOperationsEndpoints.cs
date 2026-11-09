using WalletPK.Ledger.Application.Invariants;
using WalletPK.Ledger.Infrastructure.Audit;
using WalletPK.Ledger.Infrastructure.Invariants;
using WalletPK.Web.Authentication;

namespace WalletPK.Ledger.Api.Endpoints;

/// <summary>
/// Back-office checks of the books (ops, compliance, finance and admin roles only):
/// <list type="bullet">
/// <item>GET /api/ledger/invariants   run every invariant check now on one consistent snapshot</item>
/// <item>GET /api/ledger/audit        verify the whole audit hash chain</item>
/// </list>
/// Both always answer 200; the body's "healthy" / "intact" field carries the verdict.
/// </summary>
public static class LedgerOperationsEndpoints
{
    public static IEndpointRouteBuilder MapLedgerOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ledger").WithTags("Ledger operations").RequireAuthorization(WalletPkPolicies.Operations);

        group.MapGet("/invariants", async (ILedgerInvariantChecker checker, LedgerMetrics metrics, CancellationToken cancellationToken) =>
        {
            var report = await checker.CheckAsync(cancellationToken);
            metrics.Record(report);
            return TypedResults.Ok(report);
        });

        group.MapGet("/audit", async (AuditChainVerifier verifier, CancellationToken cancellationToken) =>
            TypedResults.Ok(await verifier.VerifyAsync(cancellationToken)));

        return endpoints;
    }
}
