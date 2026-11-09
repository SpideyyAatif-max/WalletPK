using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Concurrency;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Domain;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Holds;

/// <summary>Reserve money in a wallet.</summary>
/// <param name="AccountId">The wallet.</param>
/// <param name="Amount">How much to reserve.</param>
/// <param name="Description">What it is for.</param>
/// <param name="Reference">Business reference, for example a payment ID.</param>
/// <param name="Lifetime">How long until it expires automatically (1 minute to 7 days).</param>
public sealed record PlaceHoldCommand(AccountId AccountId, Money Amount, string Description, string? Reference, TimeSpan Lifetime);

/// <summary>Settle a hold: debit <paramref name="Amount"/> from the held wallet and credit <paramref name="CreditLines"/>.</summary>
/// <param name="HoldId">The hold.</param>
/// <param name="TransactionId">ID of the journal to post; a retry with the same ID changes nothing.</param>
/// <param name="Amount">How much to capture (at most the held amount; the rest is returned).</param>
/// <param name="Type">What the journal records.</param>
/// <param name="Description">Journal description.</param>
/// <param name="CreditLines">Where the money goes; their total must equal <paramref name="Amount"/>.</param>
public sealed record CaptureHoldCommand(
    HoldId HoldId,
    TransactionId TransactionId,
    Money Amount,
    JournalType Type,
    string Description,
    IReadOnlyList<PostingLine> CreditLines);

/// <param name="HoldId">The hold.</param>
/// <param name="AccountId">The wallet.</param>
/// <param name="Amount">Reserved amount.</param>
/// <param name="CapturedAmount">Debited amount (after capture).</param>
/// <param name="Status">Active, Captured, Released or Expired.</param>
/// <param name="ExpiresAt">When it expires if still active.</param>
/// <param name="AvailableAfter">The wallet's spendable balance after this operation.</param>
public sealed record HoldView(HoldId HoldId, AccountId AccountId, Money Amount, Money CapturedAmount, HoldStatus Status, DateTimeOffset ExpiresAt, Money AvailableAfter)
{
    public static HoldView From(Hold hold, Account account)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(account);
        return new HoldView(hold.Id, hold.AccountId, Money.FromPaisa(hold.Amount), Money.FromPaisa(hold.CapturedAmount), hold.Status, hold.ExpiresAt, Money.FromPaisa(account.Available));
    }
}

/// <summary>Places, captures, releases and expires holds (M3.5), under the same wallet locks as postings.</summary>
public sealed class HoldService(
    IHoldRepository holds,
    IAccountRepository accounts,
    IJournalRepository journals,
    ILedgerUnitOfWork unitOfWork,
    WalletConcurrency concurrency,
    TimeProvider timeProvider)
{
    public async Task<Result<HoldView>> PlaceAsync(PlaceHoldCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Amount.Currency.Code != Currency.Pkr.Code)
        {
            return LedgerErrors.CurrencyMismatch;
        }

        return await concurrency.RunAsync<HoldView>([command.AccountId], async (tokens, ct) =>
        {
            var account = await accounts.GetByIdAsync(command.AccountId, ct);
            if (account is null)
            {
                return LedgerErrors.AccountNotFound(command.AccountId);
            }

            var fenced = WalletConcurrency.ApplyFencingTokens([account], tokens);
            if (fenced.IsFailure)
            {
                return fenced.Error;
            }

            var placed = Hold.Place(account, command.Amount.MinorUnits, command.Description, command.Reference, command.Lifetime, timeProvider.GetUtcNow());
            if (placed.IsFailure)
            {
                return placed.Error;
            }

            holds.Add(placed.Value);
            await unitOfWork.SaveChangesAsync(ct);
            return HoldView.From(placed.Value, account);
        }, cancellationToken);
    }

    /// <summary>
    /// Captures in one transaction: the hold is closed, its whole reservation returned, and a journal
    /// debits the captured amount from the wallet and credits the destinations.
    /// </summary>
    public async Task<Result<PostedJournal>> CaptureAsync(CaptureHoldCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await journals.GetAsync(command.TransactionId, cancellationToken) is { } existing)
        {
            return PostedJournal.From(existing, alreadyPosted: true);
        }

        // Only to find the wallet to lock; the hold is read again (fresh) under the lock.
        var hold = await holds.PeekAsync(command.HoldId, cancellationToken);
        if (hold is null)
        {
            return LedgerErrors.HoldNotFound;
        }

        if (command.CreditLines.Count == 0 ||
            command.CreditLines.Any(line => line.Direction != EntryDirection.Credit || line.Amount.Currency.Code != Currency.Pkr.Code) ||
            command.CreditLines.Sum(line => line.Amount.MinorUnits) != command.Amount.MinorUnits)
        {
            return LedgerErrors.Unbalanced;
        }

        var walletIds = command.CreditLines
            .Where(line => line.Account.AccountId is not null)
            .Select(line => line.Account.AccountId!.Value)
            .Append(hold.AccountId)
            .Distinct()
            .ToList();

        return await concurrency.RunAsync<PostedJournal>(walletIds, async (tokens, ct) =>
        {
            // Re-read under the lock: another request may have captured or released it meanwhile.
            var current = await holds.GetAsync(command.HoldId, ct);
            if (current is null)
            {
                return LedgerErrors.HoldNotFound;
            }

            var resolved = command.CreditLines
                .Select(line => (Line: line, AccountId: line.Account.Resolve(command.TransactionId)))
                .ToList();
            var loaded = await AccountLookup.LoadAllAsync(accounts, [.. resolved.Select(item => item.AccountId).Append(current.AccountId).Distinct()], ct);
            if (loaded.IsFailure)
            {
                return loaded.Error;
            }

            var fenced = WalletConcurrency.ApplyFencingTokens(loaded.Value.Values, tokens);
            if (fenced.IsFailure)
            {
                return fenced.Error;
            }

            var now = timeProvider.GetUtcNow();
            var wallet = loaded.Value[current.AccountId];
            var captured = current.Capture(wallet, command.Amount.MinorUnits, command.TransactionId, now);
            if (captured.IsFailure)
            {
                return captured.Error;
            }

            List<JournalLine> lines =
            [
                new JournalLine(wallet, EntryDirection.Debit, command.Amount.MinorUnits),
                .. resolved.Select(item => new JournalLine(loaded.Value[item.AccountId], EntryDirection.Credit, item.Line.Amount.MinorUnits)),
            ];
            var posted = JournalTransaction.Post(command.TransactionId, command.Type, command.Description, current.Reference, lines, now);
            if (posted.IsFailure)
            {
                return posted.Error;
            }

            journals.Add(posted.Value);
            await unitOfWork.SaveChangesAsync(ct);
            return PostedJournal.From(posted.Value, alreadyPosted: false);
        }, cancellationToken);
    }

    /// <summary>Cancels a hold and returns all of its money to the wallet.</summary>
    public Task<Result<HoldView>> ReleaseAsync(HoldId holdId, CancellationToken cancellationToken) =>
        CloseAsync(holdId, expire: false, cancellationToken);

    /// <summary>
    /// The sweeper's job: returns the money of every hold whose time ran out (in batches). Each hold is
    /// released in its own transaction, so one failure never blocks the others.
    /// </summary>
    public async Task<int> ExpireDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        var expired = 0;
        foreach (var holdId in await holds.ListExpiredAsync(timeProvider.GetUtcNow(), batchSize, cancellationToken))
        {
            if ((await CloseAsync(holdId, expire: true, cancellationToken)).IsSuccess)
            {
                expired++;
            }
        }

        return expired;
    }

    private async Task<Result<HoldView>> CloseAsync(HoldId holdId, bool expire, CancellationToken cancellationToken)
    {
        // Only to find the wallet to lock; the hold is read again (fresh) under the lock.
        var hold = await holds.PeekAsync(holdId, cancellationToken);
        if (hold is null)
        {
            return LedgerErrors.HoldNotFound;
        }

        return await concurrency.RunAsync<HoldView>([hold.AccountId], async (tokens, ct) =>
        {
            var current = await holds.GetAsync(holdId, ct);
            var account = current is null ? null : await accounts.GetByIdAsync(current.AccountId, ct);
            if (current is null || account is null)
            {
                return LedgerErrors.HoldNotFound;
            }

            var fenced = WalletConcurrency.ApplyFencingTokens([account], tokens);
            if (fenced.IsFailure)
            {
                return fenced.Error;
            }

            var now = timeProvider.GetUtcNow();
            var closed = expire ? current.Expire(account, now) : current.Release(account, now);
            if (closed.IsFailure)
            {
                return closed.Error;
            }

            await unitOfWork.SaveChangesAsync(ct);
            return HoldView.From(current, account);
        }, cancellationToken);
    }
}
