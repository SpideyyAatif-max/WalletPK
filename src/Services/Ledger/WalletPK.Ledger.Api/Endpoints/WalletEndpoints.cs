using Microsoft.AspNetCore.Mvc;
using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Web.Errors;
using WalletPK.Web.Idempotency;
using WalletPK.Web.Identity;

namespace WalletPK.Ledger.Api.Endpoints;

/// <summary>
/// The signed-in customer's wallet (access token required). Amounts are in paisa (Rs. 1 = 100 paisa);
/// money in responses is <c>{"minorUnits":150000,"currency":"PKR"}</c>.
/// <list type="bullet">
/// <item>POST /api/wallets                        open my wallet (201; 200 if it already exists)</item>
/// <item>GET  /api/wallets/me                     balance, held and available</item>
/// <item>GET  /api/wallets/me/transactions        statement, newest first (?limit=20&amp;before={nextBefore})</item>
/// <item>GET  /api/wallets/transfers/quote        fee and tax for ?amountPaisa=</item>
/// <item>POST /api/wallets/transfers              send money to a mobile number (Idempotency-Key required)</item>
/// </list>
/// </summary>
public static class WalletEndpoints
{
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/wallets").WithTags("Wallets").RequireAuthorization();

        group.MapPost("", async (ICurrentUser currentUser, WalletService wallets, CancellationToken cancellationToken) =>
        {
            var result = await wallets.OpenAsync(currentUser.GetRequiredId(), currentUser.PhoneNumber, cancellationToken);
            return result.ToHttpResult(opened => opened.Created
                ? TypedResults.Created("/api/wallets/me", opened.Wallet)
                : TypedResults.Ok(opened.Wallet));
        });

        group.MapGet("/me", async (ICurrentUser currentUser, WalletService wallets, CancellationToken cancellationToken) =>
            (await wallets.GetAsync(currentUser.GetRequiredId(), cancellationToken)).ToHttpResult());

        group.MapGet("/me/transactions", async (
            int? limit, long? before, ICurrentUser currentUser, WalletService wallets, CancellationToken cancellationToken) =>
            (await wallets.HistoryAsync(currentUser.GetRequiredId(), limit, before, cancellationToken)).ToHttpResult());

        group.MapGet("/transfers/quote", (long? amountPaisa, WalletService wallets) =>
            (amountPaisa is > 0 ? wallets.QuoteTransfer(Money.FromPaisa(amountPaisa.Value)) : Result.Failure<Application.Fees.FeeQuote>(WalletErrors.AmountInvalid))
                .ToHttpResult());

        group.MapPost("/transfers", async (
            TransferRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string idempotencyKey,
            ICurrentUser currentUser,
            WalletService wallets,
            CancellationToken cancellationToken) =>
        {
            if (request.AmountPaisa is not > 0)
            {
                return WalletErrors.AmountInvalid.ToProblem();
            }

            var userId = currentUser.GetRequiredId();
            var command = new TransferCommand(
                userId,
                currentUser.DeviceId,
                request.ToWalletNumber,
                Money.FromPaisa(request.AmountPaisa.Value),
                WalletService.TransactionIdFor(userId, idempotencyKey));

            var result = await wallets.TransferAsync(command, cancellationToken);
            return result.ToHttpResult();
        }).RequireIdempotencyKey();

        return endpoints;
    }
}

/// <param name="ToWalletNumber">Recipient's mobile number: "03001234567", "+923001234567" or "923001234567".</param>
/// <param name="AmountPaisa">Amount to send, in paisa. The fee and tax are added on top (see the quote).</param>
public sealed record TransferRequest(string? ToWalletNumber, long? AmountPaisa);
