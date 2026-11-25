using Microsoft.AspNetCore.Mvc;
using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Ledger.Application.Reports;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Web.Errors;
using WalletPK.Web.Idempotency;
using WalletPK.Web.Security;
using WalletPK.Web.Identity;

namespace WalletPK.Ledger.Api.Endpoints;

/// <summary>
/// The signed-in customer's wallet (access token required). Amounts are in paisa (Rs. 1 = 100 paisa);
/// money in responses is <c>{"minorUnits":150000,"currency":"PKR"}</c>.
/// <list type="bullet">
/// <item>POST /api/wallets                        open my wallet (201; 200 if it already exists)</item>
/// <item>GET  /api/wallets/me                     balance, held and available</item>
/// <item>GET  /api/wallets/me/merchant            my merchant wallet (M8), when I have a shop</item>
/// <item>GET  /api/wallets/me/agent               my agent float (M9), when I am an agent</item>
/// <item>GET  /api/wallets/me/balance             balance from the read model, with the sequence of the newest entry in it</item>
/// <item>GET  /api/wallets/me/transactions        statement, newest first (?limit=20&amp;before={nextBefore}&amp;type=P2PTransfer&amp;direction=out&amp;from=&amp;to=)</item>
/// <item>GET  /api/wallets/me/statements/{yyyy-MM}[.pdf]  a month's statement (Pakistan time), as JSON or a PDF (M12.5)</item>
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

        // The owner's merchant wallet (M8) and agent float (M9): opened by those services, read here.
        group.MapGet("/me/merchant", async (ICurrentUser currentUser, WalletService wallets, CancellationToken cancellationToken) =>
            (await wallets.GetAsync(currentUser.GetRequiredId(), AccountType.MerchantWallet, cancellationToken)).ToHttpResult());

        group.MapGet("/me/agent", async (ICurrentUser currentUser, WalletService wallets, CancellationToken cancellationToken) =>
            (await wallets.GetAsync(currentUser.GetRequiredId(), AccountType.AgentWallet, cancellationToken)).ToHttpResult());

        group.MapGet("/me/balance", async (ICurrentUser currentUser, WalletService wallets, CancellationToken cancellationToken) =>
            (await wallets.BalanceAsync(currentUser.GetRequiredId(), cancellationToken)).ToHttpResult());

        group.MapGet("/me/transactions", async (
            int? limit,
            long? before,
            string? type,
            string? direction,
            DateTimeOffset? from,
            DateTimeOffset? to,
            ICurrentUser currentUser,
            WalletService wallets,
            CancellationToken cancellationToken) =>
            (await wallets.HistoryAsync(currentUser.GetRequiredId(), new StatementQuery(limit, before, type, direction, from, to), cancellationToken)).ToHttpResult());

        // M12.5: a calendar month's statement, as data or as a PDF to keep.
        group.MapGet("/me/statements/{month}", async (string month, ICurrentUser currentUser, WalletService wallets, CancellationToken cancellationToken) =>
            (await wallets.StatementAsync(currentUser.GetRequiredId(), month, cancellationToken)).ToHttpResult());

        group.MapGet("/me/statements/{month}.pdf", async (string month, ICurrentUser currentUser, WalletService wallets, IStatementRenderer renderer, CancellationToken cancellationToken) =>
            (await wallets.StatementAsync(currentUser.GetRequiredId(), month, cancellationToken))
                .ToHttpResult(statement => Results.File(renderer.Render(statement), "application/pdf", $"walletpk-statement-{statement.Month}.pdf")));

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
        }).RequireIdempotencyKey().RequireMpinConfirmation();

        return endpoints;
    }
}

/// <param name="ToWalletNumber">Recipient's mobile number: "03001234567", "+923001234567" or "923001234567".</param>
/// <param name="AmountPaisa">Amount to send, in paisa. The fee and tax are added on top (see the quote).</param>
public sealed record TransferRequest(string? ToWalletNumber, long? AmountPaisa);
