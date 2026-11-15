using Microsoft.AspNetCore.Mvc;
using WalletPK.Common.Correlation;
using WalletPK.Common.Results;
using WalletPK.Payments.Application.BankTransfers;
using WalletPK.Payments.Application.Raast;
using WalletPK.Web.Errors;
using WalletPK.Web.Idempotency;
using WalletPK.Web.Identity;

namespace WalletPK.Payments.Api.Endpoints;

/// <param name="ToIban">The beneficiary's IBAN (from a lookup).</param>
/// <param name="AmountPaisa">How much (Rs. 1 = 100 paisa).</param>
/// <param name="BeneficiaryTitle">The name the lookup showed.</param>
/// <param name="Note">A note to the beneficiary.</param>
public sealed record BankTransferRequest(string? ToIban, long? AmountPaisa, string? BeneficiaryTitle, string? Note);

/// <summary>Give one: an IBAN, or a Raast ID (mobile number).</summary>
public sealed record BeneficiaryLookupRequest(string? Iban, string? RaastId);

/// <summary>
/// Bank transfers over Raast for the signed-in customer (access token required):
/// <list type="bullet">
/// <item>GET  /api/payments/banks                      the banks on Raast and whether each is up (its circuit breaker)</item>
/// <item>POST /api/payments/beneficiaries/lookup       who is behind an IBAN or Raast ID</item>
/// <item>POST /api/payments/bank-transfers             send (Idempotency-Key required): 202 with the transfer and its tracker</item>
/// <item>GET  /api/payments/bank-transfers/{id}        status and timeline</item>
/// <item>GET  /api/payments/bank-transfers             my transfers, newest first (?limit=20&amp;before={requestedAt})</item>
/// </list>
/// </summary>
public static class BankTransferEndpoints
{
    public static IEndpointRouteBuilder MapBankTransferEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/payments").WithTags("Bank transfers").RequireAuthorization();

        group.MapGet("/banks", (IBankAvailability banks) => TypedResults.Ok(banks.GetAll()));

        group.MapPost("/beneficiaries/lookup", async (BeneficiaryLookupRequest request, BankTransferService transfers, CancellationToken cancellationToken) =>
            (await transfers.LookupAsync(request.Iban, request.RaastId, cancellationToken)).ToHttpResult());

        group.MapPost("/bank-transfers", async (
            BankTransferRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string? idempotencyKey,
            ICurrentUser currentUser,
            BankTransferService transfers,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            {
                return Error.Validation("idempotency.key_required", $"Send a unique {IdempotencyMiddleware.HeaderName} header (at most 100 characters) with every transfer.").ToProblem();
            }

            var command = new SendBankTransferCommand(
                currentUser.GetRequiredId().Value,
                currentUser.DeviceId,
                idempotencyKey,
                request.ToIban,
                request.AmountPaisa ?? 0,
                request.BeneficiaryTitle,
                request.Note,
                CorrelationContext.Current);

            var result = await transfers.SendAsync(command, cancellationToken);
            return result.ToHttpResult(sent => sent.Created
                ? TypedResults.Accepted($"/api/payments/bank-transfers/{sent.Transfer.Id}", sent.Transfer)
                : TypedResults.Ok(sent.Transfer));
        });

        group.MapGet("/bank-transfers/{id:guid}", async (Guid id, ICurrentUser currentUser, BankTransferService transfers, CancellationToken cancellationToken) =>
            (await transfers.GetAsync(currentUser.GetRequiredId().Value, id, cancellationToken)).ToHttpResult());

        group.MapGet("/bank-transfers", async (int? limit, DateTimeOffset? before, ICurrentUser currentUser, BankTransferService transfers, CancellationToken cancellationToken) =>
            TypedResults.Ok(await transfers.ListAsync(currentUser.GetRequiredId().Value, limit, before, cancellationToken)));

        return endpoints;
    }
}
