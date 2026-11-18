using Microsoft.AspNetCore.Mvc;
using WalletPK.Common.Correlation;
using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Payments.Application.Wallets;
using WalletPK.Web.Errors;
using WalletPK.Web.Idempotency;
using WalletPK.Web.Identity;

namespace WalletPK.Payments.Api.Endpoints;

/// <param name="ToWalletNumber">The recipient's mobile number.</param>
/// <param name="AmountPaisa">What they get; the fee is added on top (GET /api/wallets/transfers/quote).</param>
/// <param name="Note">A note.</param>
public sealed record P2PTransferRequest(string? ToWalletNumber, long? AmountPaisa, string? Note);

/// <param name="FromWalletNumber">Who should pay.</param>
/// <param name="AmountPaisa">How much.</param>
/// <param name="Note">What for.</param>
/// <param name="ExpiresInHours">How long the request waits (default 7 days, at most 30).</param>
public sealed record MoneyRequestRequest(string? FromWalletNumber, long? AmountPaisa, string? Note, int? ExpiresInHours);

public sealed record DeclineRequest(string? Reason);

/// <param name="Title">What the bill is ("Dinner at Monal").</param>
/// <param name="TotalPaisa">The whole bill.</param>
/// <param name="Participants">The others, with their shares (all or none; none = equal shares).</param>
/// <param name="IncludeMe">Whether the requester has a share too (equal shares only; default true).</param>
public sealed record SplitRequest(string? Title, long? TotalPaisa, IReadOnlyList<SplitParticipant>? Participants, bool? IncludeMe);

/// <summary>
/// Wallet payments for the signed-in customer (access token required):
/// <list type="bullet">
/// <item>POST /api/payments/p2p-transfers               send money to another wallet (Idempotency-Key): 202, answer in a moment</item>
/// <item>GET  /api/payments/p2p-transfers[/{id}]         my sends / one with its timeline</item>
/// <item>POST /api/payments/requests                     ask someone for money (Idempotency-Key)</item>
/// <item>GET  /api/payments/requests?direction=incoming  requests I am asked to pay (or outgoing: that I made)</item>
/// <item>GET  /api/payments/requests/{id}                one request with its timeline</item>
/// <item>POST /api/payments/requests/{id}/pay|decline|cancel</item>
/// <item>POST /api/payments/splits                       split a bill (Idempotency-Key); GET /api/payments/splits[/{id}]</item>
/// </list>
/// </summary>
public static class WalletPaymentEndpoints
{
    public static IEndpointRouteBuilder MapWalletPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/payments").WithTags("Wallet payments").RequireAuthorization();

        group.MapPost("/p2p-transfers", async (
            P2PTransferRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string? idempotencyKey,
            ICurrentUser currentUser,
            WalletPaymentsService payments,
            CancellationToken cancellationToken) =>
        {
            if (KeyProblem(idempotencyKey) is { } problem)
            {
                return problem;
            }

            var result = await payments.SendAsync(CallerOf(currentUser), idempotencyKey!, request.ToWalletNumber, request.AmountPaisa ?? 0, request.Note, CorrelationContext.Current, cancellationToken);
            return result.ToHttpResult(sent => sent.Created
                ? TypedResults.Accepted($"/api/payments/p2p-transfers/{sent.Transfer.Id}", sent.Transfer)
                : TypedResults.Ok(sent.Transfer));
        });

        group.MapGet("/p2p-transfers/{id:guid}", async (Guid id, ICurrentUser currentUser, WalletPaymentsService payments, CancellationToken cancellationToken) =>
            (await payments.GetTransferAsync(CallerOf(currentUser), id, cancellationToken)).ToHttpResult());

        group.MapGet("/p2p-transfers", async (int? limit, DateTimeOffset? before, ICurrentUser currentUser, WalletPaymentsService payments, CancellationToken cancellationToken) =>
            TypedResults.Ok(await payments.ListTransfersAsync(CallerOf(currentUser), limit, before, cancellationToken)));

        group.MapPost("/requests", async (
            MoneyRequestRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string? idempotencyKey,
            ICurrentUser currentUser,
            WalletPaymentsService payments,
            CancellationToken cancellationToken) =>
        {
            if (KeyProblem(idempotencyKey) is { } problem)
            {
                return problem;
            }

            var result = await payments.RequestAsync(
                CallerOf(currentUser), idempotencyKey!, request.FromWalletNumber, request.AmountPaisa ?? 0, request.Note, request.ExpiresInHours, CorrelationContext.Current, cancellationToken);
            return result.ToHttpResult(created => created.Created
                ? TypedResults.Created($"/api/payments/requests/{created.Request.Id}", created.Request)
                : TypedResults.Ok(created.Request));
        });

        group.MapGet("/requests", async (string? direction, int? limit, DateTimeOffset? before, ICurrentUser currentUser, WalletPaymentsService payments, CancellationToken cancellationToken) =>
        {
            if (direction is not (null or "incoming" or "outgoing"))
            {
                return Error.Validation("payments.direction", "direction is incoming or outgoing.").ToProblem();
            }

            return (await payments.ListRequestsAsync(CallerOf(currentUser), direction != "outgoing", limit, before, cancellationToken)).ToHttpResult();
        });

        group.MapGet("/requests/{id:guid}", async (Guid id, ICurrentUser currentUser, WalletPaymentsService payments, CancellationToken cancellationToken) =>
            (await payments.GetRequestAsync(CallerOf(currentUser), id, cancellationToken)).ToHttpResult());

        group.MapPost("/requests/{id:guid}/pay", async (Guid id, ICurrentUser currentUser, WalletPaymentsService payments, CancellationToken cancellationToken) =>
            (await payments.PayAsync(CallerOf(currentUser), id, cancellationToken)).ToHttpResult(view => TypedResults.Accepted($"/api/payments/requests/{id}", view)));

        group.MapPost("/requests/{id:guid}/decline", async (Guid id, DeclineRequest? request, ICurrentUser currentUser, WalletPaymentsService payments, CancellationToken cancellationToken) =>
            (await payments.DeclineAsync(CallerOf(currentUser), id, request?.Reason, cancellationToken)).ToHttpResult());

        group.MapPost("/requests/{id:guid}/cancel", async (Guid id, ICurrentUser currentUser, WalletPaymentsService payments, CancellationToken cancellationToken) =>
            (await payments.CancelAsync(CallerOf(currentUser), id, cancellationToken)).ToHttpResult());

        group.MapPost("/splits", async (
            SplitRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string? idempotencyKey,
            ICurrentUser currentUser,
            WalletPaymentsService payments,
            CancellationToken cancellationToken) =>
        {
            if (KeyProblem(idempotencyKey) is { } problem)
            {
                return problem;
            }

            var result = await payments.SplitAsync(
                CallerOf(currentUser), idempotencyKey!, request.Title, request.TotalPaisa ?? 0, request.Participants, request.IncludeMe ?? true, CorrelationContext.Current, cancellationToken);
            return result.ToHttpResult(created => created.Created
                ? TypedResults.Created($"/api/payments/splits/{created.Split.Id}", created.Split)
                : TypedResults.Ok(created.Split));
        });

        group.MapGet("/splits/{id:guid}", async (Guid id, ICurrentUser currentUser, WalletPaymentsService payments, CancellationToken cancellationToken) =>
            (await payments.GetSplitAsync(CallerOf(currentUser), id, cancellationToken)).ToHttpResult());

        group.MapGet("/splits", async (int? limit, ICurrentUser currentUser, WalletPaymentsService payments, CancellationToken cancellationToken) =>
            TypedResults.Ok(await payments.ListSplitsAsync(CallerOf(currentUser), limit, cancellationToken)));

        return endpoints;
    }

    private static Caller CallerOf(ICurrentUser user) =>
        new(user.GetRequiredId().Value, PakistaniMobileNumber.TryNormalize(user.PhoneNumber, out var number) ? number : null, user.DeviceId);

    private static IResult? KeyProblem(string? idempotencyKey) =>
        string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100
            ? Error.Validation("idempotency.key_required", $"Send a unique {IdempotencyMiddleware.HeaderName} header (at most 100 characters).").ToProblem()
            : null;
}
