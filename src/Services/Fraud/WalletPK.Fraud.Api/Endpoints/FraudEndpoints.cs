using System.Text;
using System.Text.Json;
using WalletPK.Common.Time;
using WalletPK.Fraud.Application;
using WalletPK.Fraud.Domain;
using WalletPK.Web.Authentication;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Fraud.Api.Endpoints;

/// <param name="Id">Lowercase letters, digits and dashes ("cash-out-burst").</param>
/// <param name="Name">For the rule list.</param>
/// <param name="Description">What it catches.</param>
/// <param name="Definition">The rule itself, a JSON object (see the rules documentation).</param>
public sealed record CreateRuleRequest(string? Id, string? Name, string? Description, JsonElement? Definition);

/// <summary>Anything left out stays as it is.</summary>
public sealed record UpdateRuleRequest(string? Name, string? Description, JsonElement? Definition, bool? Enabled);

/// <param name="Rule">A rule to try (a JSON object), or none to try the rules in force.</param>
/// <param name="Features">Feature values by name.</param>
public sealed record TestRuleRequest(JsonElement? Rule, Dictionary<string, JsonElement>? Features);

public sealed record OpenCaseRequest(Guid? UserId, string? Title);

public sealed record NoteRequest(string? Text);

/// <param name="Resolution">"ConfirmedFraud" or "FalsePositive".</param>
/// <param name="Unfreeze">For a false positive: unfreeze the case's users.</param>
/// <param name="Note">A closing note.</param>
public sealed record CloseCaseRequest(string? Resolution, bool? Unfreeze, string? Note);

public sealed record ReasonRequest(string? Reason);

/// <summary>
/// The compliance API (M10.2, M10.6, M10.7; compliance officers and admins only):
/// <list type="bullet">
/// <item>GET  /api/fraud/features                    what rules can test</item>
/// <item>GET  /api/fraud/rules[/{id}]                the rules; POST to add one, PUT /{id} to change or switch one off</item>
/// <item>POST /api/fraud/rules/test                  try a rule (or the rules in force) on some feature values</item>
/// <item>GET  /api/fraud/alerts?userId=&amp;severity=   the newest alerts</item>
/// <item>GET  /api/fraud/cases?status=               cases; POST to open one; GET /{id}; POST /{id}/assign, /notes, /close</item>
/// <item>GET  /api/fraud/accounts/{userId}           freeze status and alerts; POST /freeze, /unfreeze with a reason</item>
/// <item>GET  /api/fraud/audit[?after=]              the audit log; /audit/recent?before= newest first; /audit/verify checks its hash chain</item>
/// <item>GET  /api/fraud/reports/suspicious.csv?from=&amp;to=&amp;minSeverity=   the period's alerts for review (M12.7)</item>
/// <item>GET  /api/fraud/cases/{id}/report           a case written up as a suspicious-activity report (M12.7)</item>
/// </list>
/// </summary>
public static class FraudEndpoints
{
    public static IEndpointRouteBuilder MapFraudEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/fraud").WithTags("Fraud & compliance").RequireAuthorization(WalletPkPolicies.Compliance);

        group.MapGet("/features", () => TypedResults.Ok(FeatureCatalog.All.Select(feature => new { feature.Name, Kind = feature.Kind.ToString(), feature.Description })));

        // ---- Rules (M10.2) ----
        group.MapGet("/rules", async (RuleService rules, CancellationToken cancellationToken) => TypedResults.Ok(await rules.ListAsync(cancellationToken)));

        group.MapGet("/rules/{id}", async (string id, RuleService rules, CancellationToken cancellationToken) => (await rules.GetAsync(id, cancellationToken)).ToHttpResult());

        group.MapPost("/rules", async (CreateRuleRequest request, ICurrentUser user, RuleService rules, CancellationToken cancellationToken) =>
            (await rules.CreateAsync(request.Id, request.Name, request.Description, request.Definition?.GetRawText(), Actor(user), cancellationToken))
                .ToHttpResult(rule => TypedResults.Created($"/api/fraud/rules/{rule.Id}", rule)));

        group.MapPut("/rules/{id}", async (string id, UpdateRuleRequest request, ICurrentUser user, RuleService rules, CancellationToken cancellationToken) =>
            (await rules.UpdateAsync(id, request.Name, request.Description, request.Definition?.GetRawText(), request.Enabled, Actor(user), cancellationToken)).ToHttpResult());

        group.MapPost("/rules/test", (TestRuleRequest request, RuleService rules) =>
            rules.Test(new RuleTestRequest(request.Rule?.GetRawText(), request.Features))
                .ToHttpResult(result => TypedResults.Ok(new { result.Score, Action = result.Action.ToString(), result.Rules, result.Reason })));

        // ---- Alerts and accounts (M10.6) ----
        group.MapGet("/alerts", async (Guid? userId, string? severity, int? limit, IFraudStore store, CancellationToken cancellationToken) =>
            TypedResults.Ok(await store.AlertsAsync(userId, null, severity, Math.Clamp(limit ?? 50, 1, 200), cancellationToken)));

        group.MapGet("/accounts/{userId:guid}", async (Guid userId, AccountService accounts, IFraudStore store, CancellationToken cancellationToken) =>
        {
            var flag = await accounts.StatusAsync(userId, cancellationToken);
            return TypedResults.Ok(new
            {
                UserId = userId,
                Frozen = flag?.Frozen ?? false,
                flag?.Reason,
                flag?.ChangedBy,
                flag?.ChangedAt,
                LedgerConfirmed = flag?.ConfirmedAt is not null,
                Alerts = await store.AlertsAsync(userId, null, null, 20, cancellationToken),
            });
        });

        group.MapPost("/accounts/{userId:guid}/freeze", async (Guid userId, ReasonRequest request, ICurrentUser user, AccountService accounts, CancellationToken cancellationToken) =>
            (await accounts.FreezeAsync(userId, request.Reason, Actor(user), cancellationToken)).ToHttpResult());

        group.MapPost("/accounts/{userId:guid}/unfreeze", async (Guid userId, ReasonRequest request, ICurrentUser user, AccountService accounts, CancellationToken cancellationToken) =>
            (await accounts.UnfreezeAsync(userId, request.Reason, Actor(user), cancellationToken)).ToHttpResult());

        // ---- Cases (M10.7) ----
        group.MapGet("/cases", async (string? status, int? limit, CaseService cases, CancellationToken cancellationToken) =>
            TypedResults.Ok(await cases.ListAsync(status, limit, cancellationToken)));

        group.MapGet("/cases/{id:guid}", async (Guid id, CaseService cases, CancellationToken cancellationToken) => (await cases.GetAsync(id, cancellationToken)).ToHttpResult());

        group.MapPost("/cases", async (OpenCaseRequest request, ICurrentUser user, CaseService cases, CancellationToken cancellationToken) =>
            request.UserId is not { } userId
                ? Common.Results.Error.Validation("fraud.user_required", "Say whose case it is (userId).").ToProblem()
                : (await cases.OpenAsync(userId, request.Title, Actor(user), cancellationToken)).ToHttpResult(view => TypedResults.Created($"/api/fraud/cases/{view.Case.Id}", view)));

        group.MapPost("/cases/{id:guid}/assign", async (Guid id, ICurrentUser user, CaseService cases, CancellationToken cancellationToken) =>
            (await cases.AssignAsync(id, Actor(user), cancellationToken)).ToHttpResult());

        group.MapPost("/cases/{id:guid}/notes", async (Guid id, NoteRequest request, ICurrentUser user, CaseService cases, CancellationToken cancellationToken) =>
            (await cases.NoteAsync(id, request.Text, Actor(user), cancellationToken)).ToHttpResult());

        group.MapPost("/cases/{id:guid}/close", async (Guid id, CloseCaseRequest request, ICurrentUser user, CaseService cases, CancellationToken cancellationToken) =>
            (await cases.CloseAsync(id, request.Resolution, request.Unfreeze ?? false, request.Note, Actor(user), cancellationToken)).ToHttpResult());

        // ---- Audit log (FR-5.3) ----
        group.MapGet("/audit", async (long? after, int? limit, AuditService audit, CancellationToken cancellationToken) =>
            TypedResults.Ok(await audit.ListAsync(after, limit, cancellationToken)));

        group.MapGet("/audit/recent", async (long? before, int? limit, IFraudStore store, CancellationToken cancellationToken) =>
            TypedResults.Ok(await store.AuditRecentAsync(before, Math.Clamp(limit ?? 50, 1, 500), cancellationToken)));

        // ---- Suspicious-activity reports (M12.7); every export is audited ----
        group.MapGet("/reports/suspicious.csv", async (DateOnly? from, DateOnly? to, string? minSeverity, ICurrentUser user, SuspiciousActivityService reports, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var last = to ?? timeProvider.GetBusinessDate();
            var first = from ?? last.AddDays(-29);
            return (await reports.AlertsCsvAsync(first, last, minSeverity, Actor(user), cancellationToken))
                .ToHttpResult(csv => Results.File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"walletpk-suspicious-activity-{first:yyyyMMdd}-{last:yyyyMMdd}.csv"));
        });

        group.MapGet("/cases/{id:guid}/report", async (Guid id, ICurrentUser user, SuspiciousActivityService reports, CancellationToken cancellationToken) =>
            (await reports.CaseReportAsync(id, Actor(user), cancellationToken)).ToHttpResult());

        group.MapGet("/audit/verify", async (AuditService audit, CancellationToken cancellationToken) =>
        {
            var (intact, entries, brokenAt) = await audit.VerifyAsync(cancellationToken);
            return TypedResults.Ok(new { Intact = intact, Entries = entries, BrokenAt = brokenAt });
        });

        return endpoints;
    }

    /// <summary>Who did it, for the audit log and cases.</summary>
    private static string Actor(ICurrentUser user) => $"user:{user.GetRequiredId().Value}";
}
