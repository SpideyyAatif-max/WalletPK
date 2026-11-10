using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Results;
using WalletPK.Ledger.Domain.Accounts;

namespace WalletPK.Ledger.Domain.Journals;

/// <summary>One line of a journal before it is posted: which account, which side, how much (paisa).</summary>
public sealed record JournalLine(Account Account, EntryDirection Direction, long Amount);

/// <summary>
/// A double-entry journal: one business event (a transfer, a cash-in...) recorded as two or more
/// entries whose debits equal their credits, so money is only ever moved, never created or destroyed.
/// <para>
/// Posted journals are immutable: entries are never updated or deleted (the database refuses it).
/// A mistake is corrected by posting a <see cref="JournalType.Reversal"/>.
/// </para>
/// </summary>
public sealed class JournalTransaction : AggregateRoot<TransactionId>
{
    public const int MaxLines = 20;
    public const int DescriptionMaxLength = 200;
    public const int ReferenceMaxLength = 100;

    private readonly List<JournalEntry> _entries = [];

    private JournalTransaction(TransactionId id, JournalType type, string description, string? reference, long amount, DateTimeOffset postedAt)
        : base(id)
    {
        Type = type;
        Description = description;
        Reference = reference;
        Amount = amount;
        Currency = Account.PkrCurrency;
        PostedAt = postedAt;
    }

    /// <summary>For EF Core materialisation only.</summary>
    private JournalTransaction()
    {
    }

    public JournalType Type { get; private set; }

    public string Description { get; private set; } = null!;

    /// <summary>The business reference this journal belongs to, for example a payment or bill ID.</summary>
    public string? Reference { get; private set; }

    /// <summary>Total of the debits (equal to the total of the credits), in paisa.</summary>
    public long Amount { get; private set; }

    public string Currency { get; private set; } = null!;

    public DateTimeOffset PostedAt { get; private set; }

    public IReadOnlyList<JournalEntry> Entries => _entries.AsReadOnly();

    /// <summary>
    /// Validates the lines, applies them to the accounts and returns the posted journal.
    /// Checks everything first, so either every account changes or none does.
    /// </summary>
    public static Result<JournalTransaction> Post(
        TransactionId id,
        JournalType type,
        string? description,
        string? reference,
        IReadOnlyList<JournalLine> lines,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (string.IsNullOrWhiteSpace(description))
        {
            return LedgerErrors.DescriptionRequired;
        }

        if (lines.Count < 2)
        {
            return LedgerErrors.TooFewLines;
        }

        if (lines.Count > MaxLines)
        {
            return LedgerErrors.TooManyLines;
        }

        if (lines.Any(line => line.Amount <= 0))
        {
            return LedgerErrors.AmountInvalid;
        }

        if (lines.Any(line => line.Account.Currency != Account.PkrCurrency))
        {
            return LedgerErrors.CurrencyMismatch;
        }

        long debits = 0;
        long credits = 0;
        foreach (var line in lines)
        {
            if (line.Direction == EntryDirection.Debit)
            {
                debits = checked(debits + line.Amount);
            }
            else
            {
                credits = checked(credits + line.Amount);
            }
        }

        if (debits != credits || debits == 0)
        {
            return LedgerErrors.Unbalanced;
        }

        // Check every account against its NET change before touching any of them.
        var netChanges = lines
            .GroupBy(line => line.Account)
            .Select(group => (Account: group.Key, Net: group.Sum(line => group.Key.SignedAmount(line.Direction, line.Amount))));
        foreach (var (account, net) in netChanges)
        {
            var allowed = account.CanApply(net);
            if (allowed.IsFailure)
            {
                return allowed.Error;
            }
        }

        var journal = new JournalTransaction(id, type, description.Trim(), string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), debits, now);
        var lineNumber = 1;
        foreach (var line in lines)
        {
            var (balanceAfter, sequence) = line.Account.ApplyEntry(line.Direction, line.Amount, now);
            journal._entries.Add(new JournalEntry(id, (short)lineNumber++, line.Account.Id, sequence, line.Direction, line.Amount, balanceAfter, now));
        }

        journal.Raise(new JournalPosted(
            journal,
            [.. lines.Select(line => line.Account).Distinct().Select(account =>
                new PostedAccount(account.Id, account.Type, account.OwnerId, account.Alias))]));
        return journal;
    }
}

/// <summary>
/// One side of a journal: a debit or credit of one account. <see cref="BalanceAfter"/> is the
/// account's balance right after this entry, which makes statements and audits trivial.
/// </summary>
public sealed class JournalEntry : Entity<long>
{
    internal JournalEntry(TransactionId transactionId, short lineNumber, AccountId accountId, long accountSequence, EntryDirection direction, long amount, long balanceAfter, DateTimeOffset postedAt)
    {
        TransactionId = transactionId;
        LineNumber = lineNumber;
        AccountId = accountId;
        AccountSequence = accountSequence;
        Direction = direction;
        Amount = amount;
        BalanceAfter = balanceAfter;
        PostedAt = postedAt;
    }

    /// <summary>For EF Core materialisation only.</summary>
    private JournalEntry()
    {
    }

    public TransactionId TransactionId { get; private set; }

    /// <summary>Position within the journal, from 1.</summary>
    public short LineNumber { get; private set; }

    public AccountId AccountId { get; private set; }

    /// <summary>This entry's number in its account's history: 1, 2, 3 ... with no gaps (unique per account).</summary>
    public long AccountSequence { get; private set; }

    public EntryDirection Direction { get; private set; }

    /// <summary>Always positive, in paisa; the direction says which way it goes.</summary>
    public long Amount { get; private set; }

    public long BalanceAfter { get; private set; }

    public DateTimeOffset PostedAt { get; private set; }
}

/// <summary>
/// Raised when a journal is posted. Becomes the public <c>ledger.journal_posted</c> event, plus one
/// <c>ledger.wallet_entry_posted</c> per wallet entry (M4.2).
/// </summary>
/// <param name="Journal">The journal.</param>
/// <param name="Accounts">The accounts it touched, as they were when it was posted.</param>
public sealed record JournalPosted(JournalTransaction Journal, IReadOnlyList<PostedAccount> Accounts) : DomainEvent(Journal.PostedAt);

/// <summary>What the public events need to know about an account a journal touched.</summary>
public sealed record PostedAccount(AccountId Id, AccountType Type, UserId? OwnerId, string? Alias)
{
    public bool IsWallet => Type != AccountType.System;
}
