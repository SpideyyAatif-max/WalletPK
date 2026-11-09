using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Concurrency;
using WalletPK.Ledger.Domain;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Posting;

/// <summary>
/// The posting engine: the only way money moves in WalletPK.
/// <list type="number">
/// <item>A transaction ID that was already posted returns the original journal (safe retries).</item>
/// <item>The wallets involved are locked (Redis, with fencing tokens); system account shards are not,
/// they rely on optimistic concurrency and sharding instead.</item>
/// <item>Every line's account is resolved (picking the shard for system accounts) and loaded in one query.</item>
/// <item>The domain validates the double entry (debits = credits, positive amounts, no overdrawn
/// wallet) and applies it to the cached balances, all-or-nothing.</item>
/// <item>One database transaction saves the journal, its entries, the new balances and the
/// <c>ledger.journal_posted</c> outbox event. If an account changed meanwhile, the attempt is retried.</item>
/// </list>
/// </summary>
public sealed class PostingService(
    IAccountRepository accounts,
    IJournalRepository journals,
    ILedgerUnitOfWork unitOfWork,
    WalletConcurrency concurrency,
    TimeProvider timeProvider)
{
    public static Error ConcurrentUpdate => WalletConcurrency.ConcurrentUpdate;

    public async Task<Result<PostedJournal>> PostAsync(PostJournalCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await journals.GetAsync(command.TransactionId, cancellationToken) is { } existing)
        {
            return PostedJournal.From(existing, alreadyPosted: true);
        }

        if (command.Lines.Any(line => line.Amount.Currency.Code != Currency.Pkr.Code))
        {
            return LedgerErrors.CurrencyMismatch;
        }

        var walletIds = command.Lines
            .Where(line => line.Account.AccountId is not null)
            .Select(line => line.Account.AccountId!.Value)
            .Distinct()
            .ToList();

        return await concurrency.RunAsync(walletIds, (tokens, ct) => AttemptAsync(command, tokens, ct), cancellationToken);
    }

    private async Task<Result<PostedJournal>> AttemptAsync(
        PostJournalCommand command,
        IReadOnlyDictionary<AccountId, long> fencingTokens,
        CancellationToken cancellationToken)
    {
        var resolved = command.Lines
            .Select(line => (Line: line, AccountId: line.Account.Resolve(command.TransactionId)))
            .ToList();

        var loaded = await AccountLookup.LoadAllAsync(accounts, [.. resolved.Select(item => item.AccountId).Distinct()], cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var fenced = WalletConcurrency.ApplyFencingTokens(loaded.Value.Values, fencingTokens);
        if (fenced.IsFailure)
        {
            return fenced.Error;
        }

        var posted = JournalTransaction.Post(
            command.TransactionId,
            command.Type,
            command.Description,
            command.Reference,
            [.. resolved.Select(item => new JournalLine(loaded.Value[item.AccountId], item.Line.Direction, item.Line.Amount.MinorUnits))],
            timeProvider.GetUtcNow());
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        journals.Add(posted.Value);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateKeyException)
        {
            // The same transaction was posted by a parallel retry a moment ago: return that one.
            return await journals.GetAsync(command.TransactionId, cancellationToken) is { } winner
                ? PostedJournal.From(winner, alreadyPosted: true)
                : ConcurrentUpdate;
        }

        return PostedJournal.From(posted.Value, alreadyPosted: false);
    }
}
