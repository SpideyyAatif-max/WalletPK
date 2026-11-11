using WalletPK.Common.Results;
using WalletPK.Ledger.Application.Invariants;
using WalletPK.Ledger.Application.ReadModels;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Audit;
using WalletPK.Ledger.Infrastructure.Invariants;
using WalletPK.Web.Authentication;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Ledger.Api.Endpoints;

/// <summary>
/// Back-office checks of the books (ops, compliance, finance and admin roles only):
/// <list type="bullet">
/// <item>GET /api/ledger/invariants   run every invariant check now on one consistent snapshot</item>
/// <item>GET /api/ledger/audit        verify the whole audit hash chain</item>
/// <item>GET /api/ledger/metrics      journals and volume per minute (?from=&amp;to=&amp;type=; default the last hour, at most a day)</item>
/// <item>GET  /api/ledger/read-models         how far the read models have caught up with the ledger</item>
/// <item>POST /api/ledger/read-models/rebuild empty the read models and replay ledger.events from the start (admin only)</item>
/// </list>
/// The checks always answer 200; the body's "healthy" / "intact" field carries the verdict.
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

        group.MapGet("/metrics", async (
            DateTimeOffset? from, DateTimeOffset? to, string? type,
            IReadModelQueries queries, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var until = to ?? timeProvider.GetUtcNow();
            var since = from ?? until - TimeSpan.FromHours(1);
            if (since >= until || until - since > _maxMetricsRange)
            {
                return Error.Validation("ledger.metrics_range_invalid", "\"from\" must be before \"to\", at most one day apart.").ToProblem();
            }

            JournalType? journalType = null;
            if (type is { Length: > 0 })
            {
                if (!Enum.TryParse<JournalType>(type, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(type, out _))
                {
                    return Error.Validation("ledger.metrics_type_invalid", $"Unknown type '{type}'.").ToProblem();
                }

                journalType = parsed;
            }

            var minutes = await queries.GetMetricsAsync(since, until, journalType, cancellationToken);
            return TypedResults.Ok(new { from = since, to = until, minutes });
        });

        group.MapGet("/read-models", async (IReadModelMaintenance readModels, CancellationToken cancellationToken) =>
            TypedResults.Ok(await readModels.GetStatusAsync(cancellationToken)));

        group.MapPost("/read-models/rebuild", async (IReadModelMaintenance readModels, CancellationToken cancellationToken) =>
            {
                var generation = await readModels.RebuildAsync(cancellationToken);
                return TypedResults.Accepted("/api/ledger/read-models", new { generation });
            })
            .RequireAuthorization(policy => policy.RequireClaim(WalletPkClaimTypes.Role, "admin"));

        return endpoints;
    }

    private static readonly TimeSpan _maxMetricsRange = TimeSpan.FromDays(1);
}
