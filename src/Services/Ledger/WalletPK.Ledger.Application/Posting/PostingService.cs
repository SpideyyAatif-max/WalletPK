using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Domain;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Posting;

/// <summary>
/// The posting engine: the only way money moves in WalletPK.
/// <list type="number">
/// <item>A transaction ID that was already posted returns the original journal (safe retries).</item>
/// <item>Resolves every line's account (picking the shard for system accounts) and loads them in one query.</item>
/// <item>The domain validates the double entry (debits = credits, positive amounts, no overdrawn
/// wallet) and applies it to the cached balances, all-or-nothing.</item>
/// <item>One database transaction saves the journal, its entries, the new balances and the
/// <c>ledger.journal_posted</c> outbox event. PostgreSQL re-checks the critical rules itself.</item>
/// </list>
/// </summary>
public sealed class PostingService(
    IAccountRepository accounts,
    IJournalRepository journals,
    ILedgerUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public static Error ConcurrentUpdate { get; } =
        Error.Conflict("ledger.concurrent_update", "Another payment changed this balance at the same moment. Please try again.");

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

        var resolved = command.Lines
            .Select(line => (Line: line, AccountId: line.Account.Resolve(command.TransactionId)))
            .ToList();

        var loaded = await accounts.GetManyAsync([.. resolved.Select(item => item.AccountId).Distinct()], cancellationToken);
        var byId = loaded.ToDictionary(account => account.Id);

        var journalLines = new List<JournalLine>(resolved.Count);
        foreach (var (line, accountId) in resolved)
        {
            if (!byId.TryGetValue(accountId, out var account))
            {
                return LedgerErrors.AccountNotFound(accountId);
            }

            journalLines.Add(new JournalLine(account, line.Direction, line.Amount.MinorUnits));
        }

        var posted = JournalTransaction.Post(
            command.TransactionId,
            command.Type,
            command.Description,
            command.Reference,
            journalLines,
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
        catch (ConcurrencyConflictException)
        {
            // M3.6 adds a per-wallet lock and automatic retry; until then the caller retries.
            return ConcurrentUpdate;
        }

        return PostedJournal.From(posted.Value, alreadyPosted: false);
    }
}
