using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Results;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Domain.Accounts;

/// <summary>
/// A ledger account. Its <see cref="Balance"/> is a cached running total of its journal entries,
/// kept in the same row so reading a balance never needs to add up history. The entries remain the
/// source of truth: the invariant checker (M3.11) proves the two always agree.
/// <para>All amounts are whole paisa (Rs. 1 = 100 paisa) in a <c>long</c>; floating point is never used.</para>
/// </summary>
public sealed class Account : AggregateRoot<AccountId>
{
    public const string PkrCurrency = "PKR";

    private Account(
        AccountId id,
        AccountType type,
        string name,
        NormalBalance normalBalance,
        bool allowNegative,
        UserId? ownerId,
        string? systemCode,
        int shardIndex,
        DateTimeOffset now)
        : base(id)
    {
        Type = type;
        Name = name;
        NormalBalance = normalBalance;
        AllowNegative = allowNegative;
        OwnerId = ownerId;
        SystemCode = systemCode;
        ShardIndex = shardIndex;
        Currency = PkrCurrency;
        Status = AccountStatus.Active;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>For EF Core materialisation only.</summary>
    private Account()
    {
    }

    public AccountType Type { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>The user who owns a wallet; null for system accounts.</summary>
    public UserId? OwnerId { get; private set; }

    /// <summary>
    /// The wallet number customers send money to: the owner's mobile number in E.164 (+923001234567).
    /// Null for system accounts.
    /// </summary>
    public string? Alias { get; private set; }

    /// <summary>For system accounts, the chart-of-accounts code (for example "FEE_INCOME"); null for wallets.</summary>
    public string? SystemCode { get; private set; }

    /// <summary>Which shard of a sharded system account this is (0 for everything else).</summary>
    public int ShardIndex { get; private set; }

    public string Currency { get; private set; } = null!;

    public NormalBalance NormalBalance { get; private set; }

    /// <summary>
    /// False for every wallet: a customer can never owe WalletPK money. The database enforces it too
    /// (check constraint), so even a bug in this code cannot overdraw a wallet.
    /// </summary>
    public bool AllowNegative { get; private set; }

    /// <summary>Current balance in paisa, on the account's normal side.</summary>
    public long Balance { get; private set; }

    /// <summary>Paisa reserved by open holds (M3.5); not spendable, still part of the balance.</summary>
    public long Held { get; private set; }

    /// <summary>What can be spent right now.</summary>
    public long Available => Balance - Held;

    public AccountStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token (PostgreSQL xmin): two postings can never both update a stale balance.</summary>
    public uint Version { get; private set; }

    /// <summary>
    /// The highest wallet-lock fencing token that has written to this account (M3.6). A writer holding an
    /// older token lost its lock (it was too slow and the lock expired), so its write is refused.
    /// </summary>
    public long LockToken { get; private set; }

    public bool IsWallet => Type != AccountType.System;

    /// <summary>Opens a wallet. Wallets are credit-normal (WalletPK owes the holder that money) and can never go negative.</summary>
    public static Account OpenWallet(UserId ownerId, AccountType type, string name, DateTimeOffset now, string? alias = null)
    {
        if (type == AccountType.System)
        {
            throw new ArgumentException("Use the chart of accounts for system accounts.", nameof(type));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Account(AccountId.New(), type, name, NormalBalance.Credit, allowNegative: false, ownerId, null, 0, now)
        {
            Alias = string.IsNullOrWhiteSpace(alias) ? null : alias,
        };
    }

    /// <summary>Creates one shard of a system account from its chart-of-accounts definition.</summary>
    public static Account OpenSystemShard(SystemAccountDefinition definition, int shardIndex, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfNegative(shardIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(shardIndex, definition.Shards);

        return new Account(
            ChartOfAccounts.IdFor(definition.Code, shardIndex),
            AccountType.System,
            definition.Shards == 1 ? definition.Name : $"{definition.Name} (shard {shardIndex})",
            definition.NormalBalance,
            allowNegative: true,
            ownerId: null,
            definition.Code,
            shardIndex,
            now);
    }

    /// <summary>How an entry changes this account's balance: plus on its normal side, minus on the other.</summary>
    public long SignedAmount(EntryDirection direction, long amount) =>
        (direction == EntryDirection.Debit) == (NormalBalance == NormalBalance.Debit) ? amount : checked(-amount);

    /// <summary>Checks, without changing anything, whether the account can take this net change.</summary>
    public Result CanApply(long netChange)
    {
        if (Status == AccountStatus.Closed)
        {
            return LedgerErrors.AccountClosed(Id);
        }

        if (netChange < 0 && Status == AccountStatus.Frozen)
        {
            return LedgerErrors.AccountFrozen(Id);
        }

        if (!AllowNegative && checked(Available + netChange) < 0)
        {
            return LedgerErrors.InsufficientFunds(Id);
        }

        return Result.Success();
    }

    /// <summary>Applies one entry and returns the balance after it. Only journals call this, after <see cref="CanApply"/>.</summary>
    internal long ApplyEntry(EntryDirection direction, long amount, DateTimeOffset now)
    {
        Balance = checked(Balance + SignedAmount(direction, amount));
        UpdatedAt = now;
        return Balance;
    }

    /// <summary>
    /// Records the fencing token of the lock this write is made under. Fails if a newer lock holder has
    /// already written: the caller's lock expired and someone else took over.
    /// </summary>
    public Result ApplyFencingToken(long token)
    {
        if (token < LockToken)
        {
            return LedgerErrors.LockLost(Id);
        }

        LockToken = token;
        return Result.Success();
    }

    /// <summary>Reserves money for a hold: it stays in the balance but can no longer be spent.</summary>
    internal Result PlaceHold(long amount, DateTimeOffset now)
    {
        if (amount <= 0)
        {
            return LedgerErrors.AmountInvalid;
        }

        var allowed = CanApply(checked(-amount));
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        Held = checked(Held + amount);
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Gives a hold's reservation back (on capture, release or expiry).</summary>
    internal void ReleaseHeld(long amount, DateTimeOffset now)
    {
        if (amount <= 0 || amount > Held)
        {
            throw new InvalidOperationException($"Cannot release {amount} paisa: only {Held} is held on account {Id}.");
        }

        Held -= amount;
        UpdatedAt = now;
    }

    public void Freeze(DateTimeOffset now)
    {
        if (Status == AccountStatus.Active)
        {
            Status = AccountStatus.Frozen;
            UpdatedAt = now;
        }
    }
}
