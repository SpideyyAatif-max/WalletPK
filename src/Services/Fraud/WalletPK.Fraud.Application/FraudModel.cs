using WalletPK.Fraud.Domain;
using WalletPK.Messaging.Events;

namespace WalletPK.Fraud.Application;

/// <summary>A risk rule as stored (M10.2): its JSON definition, editable without a redeploy.</summary>
public sealed class RuleRecord
{
    /// <summary>Stable ID, for example "velocity-burst".</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>The rule's JSON (see <see cref="RiskRule"/>).</summary>
    public string Definition { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Goes up by one on every change.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Seeded by WalletPK (it can still be edited or switched off).</summary>
    public bool BuiltIn { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = "";
}

/// <summary>Something risky (M10.6): a scored payment above the alert level, or a mule ring.</summary>
public sealed class FraudAlert
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid? TransactionId { get; set; }

    /// <summary>"Payment" or "Ring".</summary>
    public string Kind { get; set; } = "Payment";

    /// <summary>"Low" (allowed, but worth a look), "Medium" (held for review) or "High" (blocked, or a ring).</summary>
    public string Severity { get; set; } = "Low";

    public int Score { get; set; }

    /// <summary>"Allow", "Review", "Block" or "Freeze".</summary>
    public string Action { get; set; } = "Allow";

    public string[] Rules { get; set; } = [];

    public string Summary { get; set; } = "";

    public string? TransactionType { get; set; }

    public long? AmountPaisa { get; set; }

    public string? Counterparty { get; set; }

    public Guid? CaseId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A case for a compliance officer (M10.7): one user, or a ring around a mule wallet.</summary>
public sealed class FraudCase
{
    public Guid Id { get; set; }

    /// <summary>"Case 42": easier to say on the phone than a GUID.</summary>
    public long Number { get; set; }

    /// <summary>The main subject: the user whose payment was refused, or the mule.</summary>
    public Guid UserId { get; set; }

    /// <summary>Everyone involved (the ring's senders, for a ring).</summary>
    public Guid[] Subjects { get; set; } = [];

    /// <summary>"Payment", "Ring" or "Manual".</summary>
    public string Kind { get; set; } = "Payment";

    public string Title { get; set; } = "";

    /// <summary>"Open", "InReview" or "Closed".</summary>
    public string Status { get; set; } = "Open";

    /// <summary>When closed: "ConfirmedFraud" or "FalsePositive".</summary>
    public string? Resolution { get; set; }

    public string? AssignedTo { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed class CaseNote
{
    public Guid Id { get; set; }

    public Guid CaseId { get; set; }

    public string Author { get; set; } = "";

    public string Text { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Whether a user's wallets are frozen, as far as the Fraud service has asked and the Ledger has confirmed.</summary>
public sealed class AccountFlag
{
    public Guid UserId { get; set; }

    public bool Frozen { get; set; }

    public string Reason { get; set; } = "";

    public string ChangedBy { get; set; } = "";

    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>The freeze or unfreeze request sent to the Ledger.</summary>
    public Guid RequestId { get; set; }

    /// <summary>When the Ledger confirmed it (null: not yet).</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }
}

/// <summary>One entry of the hash-chained audit log (FR-5.3): each hash covers the entry and the previous hash.</summary>
public sealed class AuditEntry
{
    public long Sequence { get; set; }

    public string Actor { get; set; } = "";

    public string Action { get; set; } = "";

    public string Subject { get; set; } = "";

    public string Details { get; set; } = "";

    public DateTimeOffset At { get; set; }

    public string PreviousHash { get; set; } = "";

    public string Hash { get; set; } = "";
}

/// <summary>One payment to score (from the gRPC request).</summary>
public sealed record ScoreInput(Guid TransactionId, Guid UserId, string? DeviceId, string TransactionType, long AmountPaisa, string? Counterparty);

/// <summary>What Redis knows about the user, device and recipient before this payment.</summary>
/// <param name="Recent">The user's allowed payments in the last 24 hours: when, and how much.</param>
/// <param name="DeviceBoundAt">When this device was trusted (null: not known).</param>
/// <param name="CoolingOffUntil">End of the device's cooling-off period, if any.</param>
/// <param name="RegisteredAt">When the user registered (null: not known).</param>
/// <param name="RecipientKnown">The user has paid this recipient before.</param>
/// <param name="RecipientSenders1h">Other users who paid this recipient in the last hour (this user not counted).</param>
/// <param name="FlaggedRecently">One of the user's payments was held or blocked for risk in the last hour.</param>
/// <param name="StepUpConfirmed">The user confirmed a payment from this device with a one-time code, not yet used.</param>
public sealed record FeatureSnapshot(
    IReadOnlyList<(DateTimeOffset At, long AmountPaisa)> Recent,
    DateTimeOffset? DeviceBoundAt,
    DateTimeOffset? CoolingOffUntil,
    DateTimeOffset? RegisteredAt,
    bool RecipientKnown,
    int RecipientSenders1h,
    bool FlaggedRecently,
    bool StepUpConfirmed = false);

/// <summary>Port: the Redis feature windows (M10.1) and the score cache.</summary>
public interface IFeatureStore
{
    /// <summary>The answer already given for this transaction (a retry), if any.</summary>
    Task<RiskResult?> CachedAsync(Guid transactionId, CancellationToken cancellationToken);

    Task<FeatureSnapshot> ReadAsync(ScoreInput input, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Counts the payment in the windows (an allowed one in the user's velocity), remembers the answer (not a
    /// step-up: that is asked again once the user has confirmed), flags a hold or block, and uses up the
    /// step-up confirmation when <paramref name="usedStepUp"/>.
    /// </summary>
    Task RecordAsync(ScoreInput input, RiskResult result, DateTimeOffset now, bool usedStepUp = false);

    /// <summary>The user confirmed with a one-time code on <paramref name="deviceId"/> (or any device when null), until <paramref name="validUntil"/>.</summary>
    Task ConfirmStepUpAsync(Guid userId, Guid? deviceId, DateTimeOffset validUntil);

    Task SetDeviceAsync(Guid userId, Guid deviceId, DateTimeOffset boundAt, DateTimeOffset? coolingOffUntil);

    Task SetAccountAsync(Guid userId, string? walletNumber, DateTimeOffset registeredAt);

    Task SetOwnerAsync(string walletNumber, Guid userId);

    Task<Guid?> OwnerOfAsync(string walletNumber);

    /// <summary>Counts a payment from <paramref name="senderUserId"/> to <paramref name="recipient"/> (fan-in).</summary>
    Task AddFanInAsync(string recipient, Guid senderUserId, DateTimeOffset at);

    /// <summary>The users who paid <paramref name="recipient"/> since <paramref name="since"/>.</summary>
    Task<IReadOnlyList<Guid>> SendersAsync(string recipient, DateTimeOffset since);

    /// <summary>Which of <paramref name="userIds"/> had a payment refused for risk in the last hour.</summary>
    Task<IReadOnlyList<Guid>> FlaggedAsync(IReadOnlyList<Guid> userIds);

    /// <summary>True the first time it is called for <paramref name="recipient"/> within <paramref name="window"/> (one ring case per window).</summary>
    Task<bool> ClaimRingAsync(string recipient, TimeSpan window);
}

/// <summary>Port: the rules in force, compiled; reloaded when they change (M10.2).</summary>
public interface IRuleCache
{
    IReadOnlyList<RiskRule> Rules { get; }

    Task ReloadAsync(CancellationToken cancellationToken);

    /// <summary>Tells every instance (this one included) to reload.</summary>
    Task AnnounceChangeAsync();
}

/// <summary>Port: the fraud database.</summary>
public interface IFraudStore
{
    Task<IReadOnlyList<RuleRecord>> RulesAsync(CancellationToken cancellationToken);

    Task<RuleRecord?> RuleAsync(string id, CancellationToken cancellationToken);

    void Add(RuleRecord rule);

    void Add(FraudAlert alert);

    void Add(FraudCase fraudCase);

    void Add(CaseNote note);

    Task<FraudCase?> CaseAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The user's open (or in review) case of <paramref name="kind"/>, if any.</summary>
    Task<FraudCase?> OpenCaseAsync(Guid userId, string kind, CancellationToken cancellationToken);

    Task<IReadOnlyList<FraudCase>> CasesAsync(string? status, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<FraudAlert>> AlertsAsync(Guid? userId, Guid? caseId, string? severity, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<CaseNote>> NotesAsync(Guid caseId, CancellationToken cancellationToken);

    Task<AccountFlag?> FlagAsync(Guid userId, CancellationToken cancellationToken);

    void Add(AccountFlag flag);

    /// <summary>The next case number.</summary>
    Task<long> NextCaseNumberAsync(CancellationToken cancellationToken);

    /// <summary>Appends to the audit chain; the hash is worked out when the transaction saves (one writer at a time).</summary>
    Task AuditAsync(string actor, string action, string subject, string details, DateTimeOffset at, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditEntry>> AuditTrailAsync(long? after, int limit, CancellationToken cancellationToken);

    /// <summary>Queues a message in the outbox: it is sent if, and only if, the next save commits.</summary>
    void Publish(IntegrationEvent message, string? correlationId);

    /// <summary>
    /// Inside a transaction: makes other transactions that touch these users' freezes and cases wait until
    /// this one ends. Takes the locks in a fixed order, so call it once, with every user, before anything else.
    /// </summary>
    Task LockUsersAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="work"/> and saves, in one transaction (the audit chain needs it).</summary>
    Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Fraud settings, from "Fraud".</summary>
public sealed class FraudOptions
{
    public const string SectionName = "Fraud";

    /// <summary>Scores at or above this need the user to confirm the payment with a one-time code.</summary>
    public int StepUpAt { get; set; } = 30;

    /// <summary>Scores at or above this hold the payment for review.</summary>
    public int ReviewAt { get; set; } = 60;

    /// <summary>Scores at or above this block the payment and freeze the wallets.</summary>
    public int BlockAt { get; set; } = 90;

    /// <summary>The policy these thresholds make.</summary>
    public RiskPolicy Policy() => new(ReviewAt, BlockAt, StepUpAt);

    /// <summary>Allowed (or stepped-up) payments at or above this score still raise a low alert.</summary>
    public int AlertAt { get; set; } = 40;

    /// <summary>Ring detection: senders to one wallet within this many minutes...</summary>
    public int RingWindowMinutes { get; set; } = 10;

    /// <summary>...at least this many of them...</summary>
    public int RingMinSenders { get; set; } = 5;

    /// <summary>...of whom at least this many were refused for risk in the last hour: a mule ring.</summary>
    public int RingMinFlagged { get; set; } = 3;

    /// <summary>Rules are also reloaded this often, in case a change notice was missed.</summary>
    public int RulesPollSeconds { get; set; } = 30;

    public bool IsValid() =>
        AlertAt is > 0 and <= 100 && StepUpAt is > 0 and <= 100 && ReviewAt is > 0 and <= 100 && BlockAt is > 0 and <= 100 && StepUpAt <= ReviewAt && ReviewAt <= BlockAt
        && RingWindowMinutes >= 1 && RingMinSenders >= 2 && RingMinFlagged >= 1 && RingMinFlagged <= RingMinSenders && RulesPollSeconds >= 1;
}
