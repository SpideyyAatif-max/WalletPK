using WalletPK.Common.Time;
using WalletPK.Reconciliation.Application;
using WalletPK.Web.Authentication;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Reconciliation.Api.Endpoints;

/// <param name="Date">The day to reconcile (Pakistan time); yesterday when left out.</param>
/// <param name="Parties">Which parties, e.g. ["bank:HBL"]; every party when left out.</param>
public sealed record StartRunRequest(DateOnly? Date, IReadOnlyList<string>? Parties);

/// <param name="Note">Why (5 to 1,000 characters; it goes on the audit log).</param>
public sealed record BreakNoteRequest(string? Note);

/// <param name="Resolution">Adjusted, Recovered, WrittenOff or NoActionNeeded.</param>
/// <param name="Note">What was done (5 to 1,000 characters).</param>
public sealed record ResolveBreakRequest(string? Resolution, string? Note);

/// <summary>
/// Reconciliation (M12), for back-office staff; starting runs and working breaks needs the finance role:
/// <list type="bullet">
/// <item>GET  /api/reconciliation/parties                 who sends settlement files</item>
/// <item>GET  /api/reconciliation/days[?days=14]          the recent days at a glance</item>
/// <item>GET  /api/reconciliation/summary?date=           one day: every party's latest run and open breaks</item>
/// <item>GET  /api/reconciliation/runs[?date=&amp;party=]     runs, newest first; /runs/{id} with its breaks; /runs/{id}/file the CSV as fetched</item>
/// <item>POST /api/reconciliation/runs {date, parties}    reconcile now (finance)</item>
/// <item>GET  /api/reconciliation/payments/{id}           a bank transfer, bill payment or top-up as reconciliation knows it</item>
/// <item>GET  /api/reconciliation/breaks[?status=open&amp;kind=&amp;party=&amp;from=&amp;to=]  breaks; /breaks/{id} with the payment and history</item>
/// <item>POST /api/reconciliation/breaks/{id}/dispute|resolve|reopen  Finance's workflow (M12.3)</item>
/// <item>GET  /api/reconciliation/audit[?after=]          the audit log; /audit/recent?before= newest first; /audit/verify checks its hash chain</item>
/// </list>
/// </summary>
public static class ReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/reconciliation").WithTags("Reconciliation").RequireAuthorization(WalletPkPolicies.Operations);

        group.MapGet("/parties", (ReconciliationService recon) => TypedResults.Ok(recon.Parties));

        group.MapGet("/days", async (int? days, ReconciliationService recon, CancellationToken cancellationToken) =>
            TypedResults.Ok(await recon.DaysAsync(days, cancellationToken)));

        group.MapGet("/summary", async (DateOnly? date, ReconciliationService recon, TimeProvider timeProvider, CancellationToken cancellationToken) =>
            TypedResults.Ok(await recon.SummaryAsync(date ?? timeProvider.GetBusinessDate().AddDays(-1), cancellationToken)));

        // ---- Runs (M12.1, M12.2) ----
        group.MapGet("/runs", async (DateOnly? date, string? party, int? limit, ReconciliationService recon, CancellationToken cancellationToken) =>
            TypedResults.Ok(await recon.RunsAsync(date, party, limit, cancellationToken)));

        group.MapGet("/runs/{id:guid}", async (Guid id, ReconciliationService recon, CancellationToken cancellationToken) =>
            (await recon.RunDetailAsync(id, cancellationToken)).ToHttpResult());

        group.MapGet("/runs/{id:guid}/file", async (Guid id, ReconciliationService recon, CancellationToken cancellationToken) =>
            (await recon.FileContentAsync(id, cancellationToken)).ToHttpResult(content => Results.Text(content, "text/csv; charset=utf-8")));

        group.MapPost("/runs", async (StartRunRequest request, ICurrentUser user, ReconciliationService recon, TimeProvider timeProvider, CancellationToken cancellationToken) =>
                (await recon.RunAsync(request.Date ?? timeProvider.GetBusinessDate().AddDays(-1), request.Parties, Actor(user), cancellationToken)).ToHttpResult())
            .RequireAuthorization(WalletPkPolicies.Finance);

        group.MapGet("/payments/{id:guid}", async (Guid id, ReconciliationService recon, CancellationToken cancellationToken) =>
            (await recon.PaymentAsync(id, cancellationToken)).ToHttpResult());

        // ---- Breaks (M12.3) ----
        group.MapGet("/breaks", async (string? status, string? kind, string? party, DateOnly? from, DateOnly? to, int? limit, BreakService breaks, CancellationToken cancellationToken) =>
            TypedResults.Ok(await breaks.ListAsync(new BreakQuery(status, kind, party, from, to, limit ?? 200), cancellationToken)));

        group.MapGet("/breaks/{id:guid}", async (Guid id, BreakService breaks, CancellationToken cancellationToken) =>
            (await breaks.DetailAsync(id, cancellationToken)).ToHttpResult());

        var finance = group.MapGroup("/breaks/{id:guid}").RequireAuthorization(WalletPkPolicies.Finance);

        finance.MapPost("/dispute", async (Guid id, BreakNoteRequest request, ICurrentUser user, BreakService breaks, CancellationToken cancellationToken) =>
            (await breaks.DisputeAsync(id, request.Note, Actor(user), cancellationToken)).ToHttpResult());

        finance.MapPost("/resolve", async (Guid id, ResolveBreakRequest request, ICurrentUser user, BreakService breaks, CancellationToken cancellationToken) =>
            (await breaks.ResolveAsync(id, request.Resolution, request.Note, Actor(user), cancellationToken)).ToHttpResult());

        finance.MapPost("/reopen", async (Guid id, BreakNoteRequest request, ICurrentUser user, BreakService breaks, CancellationToken cancellationToken) =>
            (await breaks.ReopenAsync(id, request.Note, Actor(user), cancellationToken)).ToHttpResult());

        // ---- Audit log (FR-5.3, M12.6) ----
        group.MapGet("/audit", async (long? after, int? limit, AuditService audit, CancellationToken cancellationToken) =>
            TypedResults.Ok(await audit.ListAsync(after, limit, cancellationToken)));

        group.MapGet("/audit/recent", async (long? before, int? limit, AuditService audit, CancellationToken cancellationToken) =>
            TypedResults.Ok(await audit.RecentAsync(before, limit, cancellationToken)));

        group.MapGet("/audit/verify", async (AuditService audit, CancellationToken cancellationToken) =>
            TypedResults.Ok(await audit.VerifyAsync(cancellationToken)));

        return endpoints;
    }

    /// <summary>Who did it, for the audit log.</summary>
    private static string Actor(ICurrentUser user) => $"user:{user.GetRequiredId().Value}";
}
