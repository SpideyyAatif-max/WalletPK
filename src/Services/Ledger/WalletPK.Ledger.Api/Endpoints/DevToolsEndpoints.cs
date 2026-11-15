using Microsoft.AspNetCore.Mvc;
using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Web.Errors;
using WalletPK.Web.Idempotency;
using WalletPK.Web.Identity;

namespace WalletPK.Ledger.Api.Endpoints;

/// <summary>
/// Development-only helpers for trying the web app (M4.7). Not mapped in any other environment, so
/// they answer 404 there. Real money only arrives through agents (M9) and banks (M6).
/// <list type="bullet">
/// <item>POST /api/wallets/me/dev/top-up  {"amountPaisa": 500000}: a test cash-in to my wallet (Idempotency-Key required, at most Rs. 100,000)</item>
/// </list>
/// </summary>
public static class DevToolsEndpoints
{
    /// <summary>Rs. 100,000 per top-up.</summary>
    public const long MaxTopUpPaisa = 10_000_000;

    public static IEndpointRouteBuilder MapDevToolsEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        var group = app.MapGroup("/api/wallets/me/dev").WithTags("Development only").RequireAuthorization();

        group.MapPost("/top-up", async (
            DevTopUpRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string idempotencyKey,
            ICurrentUser currentUser,
            WalletService wallets,
            PostingService posting,
            CancellationToken cancellationToken) =>
        {
            if (request.AmountPaisa is not (> 0 and <= MaxTopUpPaisa))
            {
                return Error.Validation("wallet.dev_top_up_invalid", "amountPaisa must be between 1 and 10,000,000 (Rs. 100,000).").ToProblem();
            }

            var userId = currentUser.GetRequiredId();
            var wallet = await wallets.GetAsync(userId, cancellationToken);
            if (wallet.IsFailure)
            {
                return wallet.Error.ToProblem();
            }

            var amount = Money.FromPaisa(request.AmountPaisa.Value);
            var posted = await posting.PostAsync(
                new PostJournalCommand(
                    WalletService.TransactionIdFor(userId, $"dev-top-up:{idempotencyKey}"),
                    JournalType.CashIn,
                    "Test cash-in (development)",
                    null,
                    [
                        PostingLine.Debit(AccountRef.System(ChartOfAccounts.BankFloat), amount),
                        PostingLine.Credit(AccountRef.Account(wallet.Value.WalletId), amount),
                    ]),
                cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error.ToProblem();
            }

            return (await wallets.GetAsync(userId, cancellationToken)).ToHttpResult();
        }).RequireIdempotencyKey();

        return app;
    }
}

/// <param name="AmountPaisa">How much test money to add, in paisa.</param>
public sealed record DevTopUpRequest(long? AmountPaisa);
