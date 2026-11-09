using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Posting;

/// <summary>
/// Which account a posting line touches: a specific account (a wallet), or a system account by its
/// chart-of-accounts definition, in which case the engine picks the shard.
/// </summary>
public sealed record AccountRef
{
    private AccountRef(AccountId? accountId, SystemAccountDefinition? systemAccount)
    {
        AccountId = accountId;
        SystemAccount = systemAccount;
    }

    public AccountId? AccountId { get; }

    public SystemAccountDefinition? SystemAccount { get; }

    public static AccountRef Account(AccountId accountId) => new(accountId, null);

    public static AccountRef System(SystemAccountDefinition definition) =>
        new(null, definition ?? throw new ArgumentNullException(nameof(definition)));

    /// <summary>The concrete account this line posts to for the given transaction.</summary>
    public AccountId Resolve(TransactionId transactionId) =>
        AccountId ?? ChartOfAccounts.ShardIdFor(SystemAccount!, transactionId);
}

/// <param name="Account">The account to debit or credit.</param>
/// <param name="Direction">Debit or credit.</param>
/// <param name="Amount">Positive PKR amount.</param>
public sealed record PostingLine(AccountRef Account, EntryDirection Direction, Money Amount)
{
    public static PostingLine Debit(AccountRef account, Money amount) => new(account, EntryDirection.Debit, amount);

    public static PostingLine Credit(AccountRef account, Money amount) => new(account, EntryDirection.Credit, amount);
}

/// <summary>A request to post one double-entry journal.</summary>
/// <param name="TransactionId">Chosen by the caller, so a retry with the same ID posts nothing twice.</param>
/// <param name="Type">What the journal records.</param>
/// <param name="Description">Human-readable description, for statements and audits.</param>
/// <param name="Reference">Optional business reference (payment ID, bill reference...).</param>
/// <param name="Lines">At least one debit and one credit; debits must equal credits.</param>
public sealed record PostJournalCommand(
    TransactionId TransactionId,
    JournalType Type,
    string Description,
    string? Reference,
    IReadOnlyList<PostingLine> Lines);

/// <param name="AccountId">The account.</param>
/// <param name="Direction">Debit or credit.</param>
/// <param name="Amount">The entry amount.</param>
/// <param name="BalanceAfter">The account's balance right after this entry.</param>
public sealed record PostedEntry(AccountId AccountId, EntryDirection Direction, Money Amount, Money BalanceAfter);

/// <summary>The result of posting (or of retrying an already-posted transaction).</summary>
/// <param name="TransactionId">The journal's ID.</param>
/// <param name="Type">What it records.</param>
/// <param name="Amount">Total moved.</param>
/// <param name="PostedAt">When it was posted.</param>
/// <param name="AlreadyPosted">True when this transaction ID had been posted before; nothing was posted again.</param>
/// <param name="Entries">Every entry, in line order.</param>
public sealed record PostedJournal(
    TransactionId TransactionId,
    JournalType Type,
    Money Amount,
    DateTimeOffset PostedAt,
    bool AlreadyPosted,
    IReadOnlyList<PostedEntry> Entries)
{
    public static PostedJournal From(JournalTransaction journal, bool alreadyPosted)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return new PostedJournal(
            journal.Id,
            journal.Type,
            Money.FromPaisa(journal.Amount),
            journal.PostedAt,
            alreadyPosted,
            [.. journal.Entries
                .OrderBy(entry => entry.LineNumber)
                .Select(entry => new PostedEntry(entry.AccountId, entry.Direction, Money.FromPaisa(entry.Amount), Money.FromPaisa(entry.BalanceAfter)))]);
    }
}
