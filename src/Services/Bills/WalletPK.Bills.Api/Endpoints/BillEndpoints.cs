using Microsoft.AspNetCore.Mvc;
using WalletPK.Bills.Application.Payments;
using WalletPK.Bills.Application.Saved;
using WalletPK.Common.Correlation;
using WalletPK.Common.Results;
using WalletPK.Web.Authentication;
using WalletPK.Web.Errors;
using WalletPK.Web.Idempotency;
using WalletPK.Web.Identity;

namespace WalletPK.Bills.Api.Endpoints;

/// <param name="Biller">"KE", "LESCO", "SNGPL", "PTCL".</param>
/// <param name="ConsumerNumber">The number on the bill.</param>
public sealed record BillInquiryRequest(string? Biller, string? ConsumerNumber);

/// <param name="Biller">The biller.</param>
/// <param name="ConsumerNumber">Whose bill.</param>
/// <param name="AmountPaisa">The payable amount the inquiry showed.</param>
/// <param name="CustomerName">The account holder the inquiry showed (for the receipt).</param>
public sealed record BillPaymentRequest(string? Biller, string? ConsumerNumber, long? AmountPaisa, string? CustomerName);

public sealed record SaveBillerRequest(string? Biller, string? ConsumerNumber, string? Nickname);

public sealed record RefundRequest(string? Reason);

public sealed record ConfirmRequest(string? BillerReference);

/// <summary>
/// Bill payment for the signed-in customer (access token required):
/// <list type="bullet">
/// <item>GET    /api/bills/billers             the catalog, with each biller's availability (its circuit breaker)</item>
/// <item>POST   /api/bills/inquiry             this month's bill for a consumer number</item>
/// <item>POST   /api/bills/payments            pay it (Idempotency-Key): 202, follow it at /api/bills/payments/{id}</item>
/// <item>GET    /api/bills/payments[/{id}]      my bill payments / one with its timeline</item>
/// <item>GET|POST /api/bills/saved, DELETE /api/bills/saved/{id}   saved billers (with the last bill seen)</item>
/// </list>
/// </summary>
public static class BillEndpoints
{
    public static IEndpointRouteBuilder MapBillEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/bills").WithTags("Bills").RequireAuthorization();

        group.MapGet("/billers", (BillsService bills) => TypedResults.Ok(bills.Billers()));

        group.MapPost("/inquiry", async (BillInquiryRequest request, BillsService bills, CancellationToken cancellationToken) =>
            (await bills.InquireAsync(request.Biller, request.ConsumerNumber, cancellationToken)).ToHttpResult());

        group.MapPost("/payments", async (
            BillPaymentRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string? idempotencyKey,
            ICurrentUser currentUser,
            BillsService bills,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            {
                return Error.Validation("idempotency.key_required", $"Send a unique {IdempotencyMiddleware.HeaderName} header (at most 100 characters) with every payment.").ToProblem();
            }

            var result = await bills.PayAsync(
                currentUser.GetRequiredId().Value, currentUser.DeviceId, idempotencyKey, request.Biller, request.ConsumerNumber, request.AmountPaisa ?? 0,
                request.CustomerName, CorrelationContext.Current, cancellationToken);
            return result.ToHttpResult(paid => paid.Created
                ? TypedResults.Accepted($"/api/bills/payments/{paid.Payment.Id}", paid.Payment)
                : TypedResults.Ok(paid.Payment));
        });

        group.MapGet("/payments/{id:guid}", async (Guid id, ICurrentUser currentUser, BillsService bills, CancellationToken cancellationToken) =>
            (await bills.GetAsync(currentUser.GetRequiredId().Value, id, cancellationToken)).ToHttpResult());

        group.MapGet("/payments", async (int? limit, DateTimeOffset? before, ICurrentUser currentUser, BillsService bills, CancellationToken cancellationToken) =>
            TypedResults.Ok(await bills.ListAsync(currentUser.GetRequiredId().Value, limit, before, cancellationToken)));

        group.MapGet("/saved", async (ICurrentUser currentUser, SavedBillersService saved, CancellationToken cancellationToken) =>
            TypedResults.Ok(await saved.ListAsync(currentUser.GetRequiredId().Value, cancellationToken)));

        group.MapPost("/saved", async (SaveBillerRequest request, ICurrentUser currentUser, SavedBillersService saved, CancellationToken cancellationToken) =>
            (await saved.SaveAsync(currentUser.GetRequiredId().Value, request.Biller, request.ConsumerNumber, request.Nickname, cancellationToken)).ToHttpResult());

        group.MapDelete("/saved/{id:guid}", async (Guid id, ICurrentUser currentUser, SavedBillersService saved, CancellationToken cancellationToken) =>
            (await saved.RemoveAsync(currentUser.GetRequiredId().Value, id, cancellationToken)).ToHttpResult());

        return endpoints;
    }

    /// <summary>
    /// The operations queue (M7.5), for the operations roles:
    /// <list type="bullet">
    /// <item>GET  /api/bills/ops/dead-letters               dead-lettered payments, then those pending verification</item>
    /// <item>POST /api/bills/ops/payments/{id}/replay       ask the biller again now</item>
    /// <item>POST /api/bills/ops/payments/{id}/refund       not paid: return the money</item>
    /// <item>POST /api/bills/ops/payments/{id}/confirm      paid (seen in the settlement file): settle the money</item>
    /// </list>
    /// </summary>
    public static IEndpointRouteBuilder MapBillOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var ops = endpoints.MapGroup("/api/bills/ops").WithTags("Bills operations").RequireAuthorization(WalletPkPolicies.Operations);

        ops.MapGet("/dead-letters", async (int? limit, BillsService bills, CancellationToken cancellationToken) =>
            TypedResults.Ok(await bills.DeadLettersAsync(limit, cancellationToken)));

        ops.MapPost("/payments/{id:guid}/replay", async (Guid id, ICurrentUser currentUser, BillsService bills, CancellationToken cancellationToken) =>
            (await bills.ReplayAsync(id, OperatorOf(currentUser), cancellationToken)).ToHttpResult());

        ops.MapPost("/payments/{id:guid}/refund", async (Guid id, RefundRequest? request, ICurrentUser currentUser, BillsService bills, CancellationToken cancellationToken) =>
            (await bills.RefundAsync(id, OperatorOf(currentUser), request?.Reason, cancellationToken)).ToHttpResult());

        ops.MapPost("/payments/{id:guid}/confirm", async (Guid id, ConfirmRequest request, ICurrentUser currentUser, BillsService bills, CancellationToken cancellationToken) =>
            (await bills.ConfirmAsync(id, OperatorOf(currentUser), request.BillerReference, cancellationToken)).ToHttpResult());

        return endpoints;
    }

    private static string OperatorOf(ICurrentUser user) => $"operator {user.GetRequiredId().Value}";
}
