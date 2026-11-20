using Microsoft.AspNetCore.Mvc;
using WalletPK.Agents.Application;
using WalletPK.Agents.Infrastructure.Payouts;
using WalletPK.Common.Correlation;
using WalletPK.Common.Results;
using WalletPK.Common.Time;
using WalletPK.Web.Authentication;
using WalletPK.Web.Errors;
using WalletPK.Web.Idempotency;
using WalletPK.Web.Identity;

namespace WalletPK.Agents.Api.Endpoints;

/// <param name="ShopName">The shop's name, as customers see it.</param>
/// <param name="City">Where it is (customers find agents by city).</param>
/// <param name="Area">The area or market, optional.</param>
public sealed record ApplyRequest(string? ShopName, string? City, string? Area);

/// <param name="CustomerNumber">The customer's mobile number ("0300 1234567").</param>
/// <param name="AmountPaisa">Cash taken, in whole rupees (Rs. 100 to Rs. 50,000).</param>
public sealed record CashInRequest(string? CustomerNumber, long? AmountPaisa);

/// <param name="CustomerNumber">The customer's mobile number.</param>
/// <param name="Code">The six-digit code the customer shows.</param>
public sealed record TakeCashOutRequest(string? CustomerNumber, string? Code);

/// <param name="Direction">"ToFloat" (from my wallet) or "ToWallet" (to my wallet).</param>
/// <param name="AmountPaisa">How much.</param>
public sealed record FloatRequest(string? Direction, long? AmountPaisa);

public sealed record LowFloatRequest(long? LowFloatPaisa);

public sealed record ReasonRequest(string? Reason);

/// <summary>
/// Agents (M9, access token required):
/// <list type="bullet">
/// <item>GET  /api/agents/find?city=                  active agents in a city</item>
/// <item>GET  /api/agents/by-code/{code}              one active agent</item>
/// <item>GET  /api/agents/quote?kind=&amp;amountPaisa=    a cash-in's commission, or a cash-out's fee</item>
/// <item>POST /api/agents                             apply to be an agent (KYC L1 or above)</item>
/// <item>GET  /api/agents/me                          my agent account, with unpaid commission</item>
/// <item>PUT  /api/agents/me/low-float                my float alert level</item>
/// <item>POST /api/agents/me/cash-ins                 cash-in for a customer (Idempotency-Key): 202</item>
/// <item>POST /api/agents/me/cash-outs                take a customer's cash-out with their code (Idempotency-Key): 202</item>
/// <item>POST /api/agents/me/float                    move money between my wallet and my float (Idempotency-Key): 202</item>
/// <item>GET  /api/agents/me/transactions             my transactions as an agent</item>
/// <item>GET  /api/agents/me/payouts                  my commission payouts</item>
/// <item>GET  /api/agents/transactions[/{id}]         my cash-ins and cash-outs as a customer / one (agent or customer)</item>
/// </list>
/// Operations: /api/agents/ops/pending, /api/agents/ops/{id}/approve|reject|suspend|reactivate, /api/agents/ops/payouts/run.
/// </summary>
public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/agents").WithTags("Agents").RequireAuthorization();

        group.MapGet("/find", async (string? city, AgentService agents, CancellationToken cancellationToken) =>
            TypedResults.Ok(await agents.FindAsync(city, cancellationToken)));

        group.MapGet("/by-code/{code}", async (string code, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.ByCodeAsync(code, cancellationToken)).ToHttpResult());

        group.MapGet("/quote", (string? kind, long? amountPaisa) => AgentService.Quote(kind, amountPaisa ?? 0).ToHttpResult());

        group.MapPost("/", async (ApplyRequest request, ICurrentUser currentUser, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.ApplyAsync(Caller(currentUser), request.ShopName, request.City, request.Area, CorrelationContext.Current, cancellationToken))
                .ToHttpResult(agent => TypedResults.Created("/api/agents/me", agent)));

        group.MapGet("/me", async (ICurrentUser currentUser, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.MeAsync(currentUser.GetRequiredId().Value, cancellationToken)).ToHttpResult());

        group.MapPut("/me/low-float", async (LowFloatRequest request, ICurrentUser currentUser, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.SetLowFloatAsync(currentUser.GetRequiredId().Value, request.LowFloatPaisa ?? -1, cancellationToken)).ToHttpResult());

        group.MapPost("/me/cash-ins", async (
            CashInRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string? idempotencyKey,
            ICurrentUser currentUser,
            AgentService agents,
            CancellationToken cancellationToken) =>
        {
            if (KeyProblem(idempotencyKey) is { } problem)
            {
                return problem;
            }

            var result = await agents.CashInAsync(Caller(currentUser), idempotencyKey!, request.CustomerNumber, request.AmountPaisa ?? 0, CorrelationContext.Current, cancellationToken);
            return result.ToHttpResult(transaction => TypedResults.Accepted($"/api/agents/transactions/{transaction.Id}", transaction));
        });

        group.MapPost("/me/cash-outs", async (
            TakeCashOutRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string? idempotencyKey,
            ICurrentUser currentUser,
            AgentService agents,
            CancellationToken cancellationToken) =>
        {
            if (KeyProblem(idempotencyKey) is { } problem)
            {
                return problem;
            }

            var result = await agents.TakeCashOutAsync(Caller(currentUser), idempotencyKey!, request.CustomerNumber, request.Code, CorrelationContext.Current, cancellationToken);
            return result.ToHttpResult(transaction => TypedResults.Accepted($"/api/agents/transactions/{transaction.Id}", transaction));
        });

        group.MapPost("/me/float", async (
            FloatRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string? idempotencyKey,
            ICurrentUser currentUser,
            AgentService agents,
            CancellationToken cancellationToken) =>
        {
            if (KeyProblem(idempotencyKey) is { } problem)
            {
                return problem;
            }

            var result = await agents.MoveFloatAsync(Caller(currentUser), idempotencyKey!, request.Direction, request.AmountPaisa ?? 0, CorrelationContext.Current, cancellationToken);
            return result.ToHttpResult(transaction => TypedResults.Accepted($"/api/agents/transactions/{transaction.Id}", transaction));
        });

        group.MapGet("/me/transactions", async (int? limit, ICurrentUser currentUser, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.AgentTransactionsAsync(currentUser.GetRequiredId().Value, limit, cancellationToken)).ToHttpResult());

        group.MapGet("/me/payouts", async (int? limit, ICurrentUser currentUser, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.PayoutsAsync(currentUser.GetRequiredId().Value, limit, cancellationToken)).ToHttpResult());

        group.MapGet("/transactions", async (int? limit, ICurrentUser currentUser, AgentService agents, CancellationToken cancellationToken) =>
            TypedResults.Ok(await agents.CustomerTransactionsAsync(currentUser.GetRequiredId().Value, limit, cancellationToken)));

        group.MapGet("/transactions/{id:guid}", async (Guid id, ICurrentUser currentUser, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.TransactionAsync(currentUser.GetRequiredId().Value, id, cancellationToken)).ToHttpResult());

        // ---- Operations: approve agents, suspend them, run the commission payout now ----
        var ops = endpoints.MapGroup("/api/agents/ops").WithTags("Agent operations").RequireAuthorization(WalletPkPolicies.Operations);

        ops.MapGet("/pending", async (AgentService agents, CancellationToken cancellationToken) =>
            TypedResults.Ok(await agents.PendingAsync(cancellationToken)));

        ops.MapPost("/{id:guid}/approve", async (Guid id, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.ApproveAsync(id, CorrelationContext.Current, cancellationToken)).ToHttpResult());

        ops.MapPost("/{id:guid}/reject", async (Guid id, ReasonRequest request, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.RejectAsync(id, request.Reason, cancellationToken)).ToHttpResult());

        ops.MapPost("/{id:guid}/suspend", async (Guid id, ReasonRequest request, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.SuspendAsync(id, request.Reason, cancellationToken)).ToHttpResult());

        ops.MapPost("/{id:guid}/reactivate", async (Guid id, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.ReactivateAsync(id, cancellationToken)).ToHttpResult());

        ops.MapPost("/payouts/run", async (DateOnly? date, CommissionPayoutService payouts, TimeProvider timeProvider, CancellationToken cancellationToken) =>
            TypedResults.Ok(await payouts.RunAsync(date ?? timeProvider.GetBusinessDate().AddDays(-1), cancellationToken)));

        return endpoints;
    }

    internal static Caller Caller(ICurrentUser currentUser) =>
        new(currentUser.GetRequiredId().Value, currentUser.PhoneNumber, currentUser.DeviceId, currentUser.KycTier);

    private static IResult? KeyProblem(string? idempotencyKey) =>
        string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100
            ? Error.Validation("idempotency.key_required", $"Send a unique {IdempotencyMiddleware.HeaderName} header (at most 100 characters) with every transaction.").ToProblem()
            : null;
}
