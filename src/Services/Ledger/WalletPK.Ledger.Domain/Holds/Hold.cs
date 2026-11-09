using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Results;
using WalletPK.Ledger.Domain.Accounts;

namespace WalletPK.Ledger.Domain.Holds;

[JsonConverter(typeof(StronglyTypedIdJsonConverter<HoldId>))]
public readonly record struct HoldId(Guid Value) : IStronglyTypedId<HoldId>, IParsable<HoldId>
{
    public static HoldId New() => new(Guid.CreateVersion7());

    public static HoldId From(Guid value) => new(value);

    public static HoldId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out HoldId result)
    {
        if (Guid.TryParse(s, provider, out var guid))
        {
            result = new HoldId(guid);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

public enum HoldStatus
{
    /// <summary>Money is reserved and cannot be spent.</summary>
    Active = 0,

    /// <summary>Settled: part or all of the money was debited, the rest returned.</summary>
    Captured = 1,

    /// <summary>Cancelled: all of the money was returned (the payment failed or was abandoned).</summary>
    Released = 2,

    /// <summary>Nobody captured or released it in time; the sweeper returned the money automatically.</summary>
    Expired = 3,
}

/// <summary>
/// A hold (authorisation): reserves money in a wallet for a payment that is not final yet, for example a
/// bank transfer waiting for the bank's answer. The money stays in the balance but leaves "available",
/// so it cannot be spent twice. Then exactly one of:
/// <list type="bullet">
/// <item><b>Capture</b> (all or part): the captured amount is debited by a journal, the rest is returned.</item>
/// <item><b>Release</b>: everything is returned.</item>
/// <item><b>Expiry</b>: if neither happens in time, the sweeper releases it, so money is never stuck.</item>
/// </list>
/// </summary>
public sealed class Hold : AggregateRoot<HoldId>
{
    public const int ReferenceMaxLength = 100;
    public const int DescriptionMaxLength = 200;

    public static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7);

    private Hold(HoldId id, AccountId accountId, long amount, string description, string? reference, DateTimeOffset now, DateTimeOffset expiresAt)
        : base(id)
    {
        AccountId = accountId;
        Amount = amount;
        Description = description;
        Reference = reference;
        Status = HoldStatus.Active;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    /// <summary>For EF Core materialisation only.</summary>
    private Hold()
    {
    }

    public AccountId AccountId { get; private set; }

    /// <summary>Paisa reserved.</summary>
    public long Amount { get; private set; }

    /// <summary>Paisa actually debited on capture (0 until then).</summary>
    public long CapturedAmount { get; private set; }

    public string Description { get; private set; } = null!;

    /// <summary>The business reference, for example the payment ID.</summary>
    public string? Reference { get; private set; }

    public HoldStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When it was captured, released or expired.</summary>
    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>The journal that captured it.</summary>
    public TransactionId? CaptureTransactionId { get; private set; }

    /// <summary>Optimistic concurrency token (PostgreSQL xmin).</summary>
    public uint Version { get; private set; }

    public bool IsExpired(DateTimeOffset now) => Status == HoldStatus.Active && ExpiresAt <= now;

    /// <summary>Reserves <paramref name="amount"/> paisa in <paramref name="account"/> for <paramref name="lifetime"/>.</summary>
    public static Result<Hold> Place(Account account, long amount, string? description, string? reference, TimeSpan lifetime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (string.IsNullOrWhiteSpace(description))
        {
            return LedgerErrors.DescriptionRequired;
        }

        if (lifetime < MinLifetime || lifetime > MaxLifetime)
        {
            return LedgerErrors.HoldLifetimeInvalid;
        }

        var reserved = account.PlaceHold(amount, now);
        if (reserved.IsFailure)
        {
            return reserved.Error;
        }

        return new Hold(HoldId.New(), account.Id, amount, description.Trim(), string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), now, now + lifetime);
    }

    /// <summary>
    /// Settles the hold for <paramref name="amount"/> (at most the held amount) and gives the whole
    /// reservation back to the account. The caller must then post the journal that debits the captured
    /// amount, in the same transaction.
    /// </summary>
    public Result Capture(Account account, long amount, TransactionId transactionId, DateTimeOffset now)
    {
        var usable = EnsureActive(account, now);
        if (usable.IsFailure)
        {
            return usable;
        }

        if (amount <= 0)
        {
            return LedgerErrors.AmountInvalid;
        }

        if (amount > Amount)
        {
            return LedgerErrors.CaptureTooLarge;
        }

        account.ReleaseHeld(Amount, now);
        CapturedAmount = amount;
        CaptureTransactionId = transactionId;
        Close(HoldStatus.Captured, now);
        return Result.Success();
    }

    /// <summary>Cancels the hold and returns all of the money.</summary>
    public Result Release(Account account, DateTimeOffset now) => Return(account, HoldStatus.Released, now, allowExpired: true);

    /// <summary>Used by the sweeper: returns the money of a hold whose time ran out.</summary>
    public Result Expire(Account account, DateTimeOffset now) =>
        IsExpired(now) ? Return(account, HoldStatus.Expired, now, allowExpired: true) : LedgerErrors.HoldNotActive;

    private Result Return(Account account, HoldStatus finalStatus, DateTimeOffset now, bool allowExpired)
    {
        var usable = EnsureActive(account, allowExpired ? DateTimeOffset.MinValue : now);
        if (usable.IsFailure)
        {
            return usable;
        }

        account.ReleaseHeld(Amount, now);
        Close(finalStatus, now);
        return Result.Success();
    }

    private Result EnsureActive(Account account, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Id != AccountId)
        {
            throw new ArgumentException("The hold belongs to a different account.", nameof(account));
        }

        if (Status != HoldStatus.Active)
        {
            return LedgerErrors.HoldNotActive;
        }

        return IsExpired(now) ? LedgerErrors.HoldExpired : Result.Success();
    }

    private void Close(HoldStatus status, DateTimeOffset now)
    {
        Status = status;
        ClosedAt = now;
    }
}
