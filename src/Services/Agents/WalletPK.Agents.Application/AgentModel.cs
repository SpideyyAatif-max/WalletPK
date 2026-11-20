using System.Text;
using WalletPK.Agents.Domain;
using WalletPK.Common.Results;
using WalletPK.Messaging.Events;
using WalletPK.Sagas;

namespace WalletPK.Agents.Application;

/// <summary>A retail agent (M9.1): a shop that does cash-in and cash-out for customers, with its own float.</summary>
public sealed class Agent
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    /// <summary>"WPKA123456": the agent's float number, shown in the shop.</summary>
    public string Code { get; set; } = "";

    public string ShopName { get; set; } = "";

    public string City { get; set; } = "";

    public string? Area { get; set; }

    /// <summary>One of <see cref="AgentStatus"/>.</summary>
    public string Status { get; set; } = AgentStatus.PendingApproval;

    /// <summary>Why it was rejected or suspended.</summary>
    public string? StatusReason { get; set; }

    public Guid? WalletId { get; set; }

    /// <summary>Below this float, an alert (agents.float_low) is sent after a cash-in.</summary>
    public long LowFloatPaisa { get; set; } = AgentRules.DefaultLowFloatPaisa;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A customer's request for cash at one agent (M9.3), with its one-time code (only its hash is kept). The
/// agent types the code to take it; it is taken once, before it expires or is locked by wrong codes.
/// </summary>
public sealed class CashOutRequest
{
    public Guid Id { get; set; }

    public Guid CustomerUserId { get; set; }

    public string CustomerWalletNumber { get; set; } = "";

    public string? CustomerDeviceId { get; set; }

    public Guid AgentId { get; set; }

    public long AmountPaisa { get; set; }

    public long FeePaisa { get; set; }

    public long TaxPaisa { get; set; }

    public long CommissionPaisa { get; set; }

    public string CodeHash { get; set; } = "";

    public int FailedAttempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>The cash-out that took it (null: not taken yet). A failed one frees it again.</summary>
    public Guid? TransactionId { get; set; }
}

/// <summary>One night's commissions paid to an agent's float (M9.4).</summary>
public sealed class CommissionPayout
{
    public Guid Id { get; set; }

    public Guid AgentId { get; set; }

    public DateOnly BusinessDate { get; set; }

    public int TransactionCount { get; set; }

    public long GrossPaisa { get; set; }

    /// <summary>Withholding tax.</summary>
    public long TaxPaisa { get; set; }

    public long NetPaisa { get; set; }

    /// <summary>Pending (sent to the Ledger), Paid or Failed.</summary>
    public string Status { get; set; } = "Pending";

    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When PayAgentCommission was last sent (it is sent again while unanswered).</summary>
    public DateTimeOffset SentAt { get; set; }

    public DateTimeOffset? PaidAt { get; set; }
}

/// <summary>A transaction whose commission a payout includes (so it is never paid twice).</summary>
public sealed class PaidCommission
{
    public Guid TransactionId { get; set; }

    public Guid PayoutId { get; set; }
}

/// <summary>Completed transactions whose commission is not paid yet.</summary>
public sealed record UnpaidCommissions(int Count, long CommissionPaisa);

/// <summary>Port: agents, cash-out requests and commission payouts.</summary>
public interface IAgentStore
{
    Task<Agent?> ByOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken);

    Task<Agent?> ByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Agent?> ByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Active agents in a city (all cities when null), by shop name.</summary>
    Task<IReadOnlyList<Agent>> ActiveAsync(string? city, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Agent>> WithStatusAsync(string status, int limit, CancellationToken cancellationToken);

    void Add(Agent agent);

    void Add(CashOutRequest request);

    Task<CashOutRequest?> RequestAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The customer's requests that are not cancelled, taken or expired (tracked, to cancel them).</summary>
    Task<IReadOnlyList<CashOutRequest>> OpenRequestsAsync(Guid customerUserId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The customer's newest request (any state).</summary>
    Task<CashOutRequest?> LatestRequestAsync(Guid customerUserId, CancellationToken cancellationToken);

    /// <summary>
    /// The request a customer (by wallet number) has open at <paramref name="agentId"/>: not cancelled, not
    /// expired, not locked, and not taken (or taken by a cash-out that failed).
    /// </summary>
    Task<CashOutRequest?> OpenRequestAtAsync(string customerWalletNumber, Guid agentId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Counts a wrong code; returns the attempts so far.</summary>
    Task<int> CountFailedAttemptAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>Takes an open request for <paramref name="transactionId"/> in one statement; false when it is no longer open.</summary>
    Task<bool> TryClaimAsync(Guid requestId, Guid transactionId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<CommissionPayout>> PayoutsAsync(Guid agentId, int limit, CancellationToken cancellationToken);

    Task<UnpaidCommissions> UnpaidAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>Queues a message in the outbox: it is sent if, and only if, the next save commits.</summary>
    void Publish(IntegrationEvent message, string? correlationId);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Port: lists of Sagas of one type, by a field of their data, newest first.</summary>
public interface ISagaLists
{
    Task<IReadOnlyList<SagaSnapshot<TData>>> ByFieldAsync<TData>(string sagaType, string field, string value, int limit, CancellationToken cancellationToken)
        where TData : class;
}

/// <summary>Agent settings, from "Agents".</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agents";

    /// <summary>The lowest KYC tier that can apply to be an agent ("L0", "L1", "L2").</summary>
    public string MinimumKycTier { get; set; } = "L1";

    /// <summary>Approve applications at once (development and demos); otherwise operations approve them.</summary>
    public bool AutoApprove { get; set; }

    /// <summary>The HMAC key cash-out codes are hashed with (at least 32 characters).</summary>
    public string CashOutCodeKey { get; set; } = "";

    public byte[] CodeKey => Encoding.UTF8.GetBytes(CashOutCodeKey);

    public bool IsValid() => MinimumKycTier is "L0" or "L1" or "L2" && CashOutCodeKey.Length >= 32;
}

public sealed record TimelineStep(int Sequence, string State, string Detail, DateTimeOffset At);

public sealed record AgentView(
    Guid Id,
    string Code,
    string ShopName,
    string City,
    string? Area,
    string Status,
    string? StatusReason,
    long LowFloatPaisa,
    int UnpaidTransactions,
    long UnpaidCommissionPaisa,
    DateTimeOffset CreatedAt);

/// <summary>An agent as a customer sees it (the agent finder, and before a cash-out).</summary>
public sealed record AgentListing(string Code, string ShopName, string City, string? Area);

public sealed record AgentTransactionView(
    Guid Id,
    string Kind,
    string Status,
    string Step,
    string AgentCode,
    string AgentName,
    string? CustomerWalletNumber,
    string? CustomerName,
    long AmountPaisa,
    long FeePaisa,
    long TaxPaisa,
    long CommissionPaisa,
    long? AgentBalancePaisa,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<TimelineStep>? Timeline);

/// <summary>
/// A cash-out request as its customer sees it. <see cref="Code"/> is only set in the answer that created it.
/// Status: Open, Processing (the agent is taking it), Completed, Failed (it can be taken again), Expired, Cancelled or Locked.
/// </summary>
public sealed record CashOutRequestView(
    Guid Id,
    string? Code,
    string Status,
    string AgentCode,
    string AgentName,
    long AmountPaisa,
    long FeePaisa,
    long TaxPaisa,
    long TotalPaisa,
    DateTimeOffset ExpiresAt,
    Guid? TransactionId,
    string? FailureReason);

public sealed record PayoutView(Guid Id, DateOnly BusinessDate, int TransactionCount, long GrossPaisa, long TaxPaisa, long NetPaisa, string Status, string? FailureReason, DateTimeOffset? PaidAt);

public static class AgentErrors
{
    public static readonly Error AlreadyAgent = Error.Conflict("agent.already_registered", "You already have an agent account.");
    public static readonly Error NotAgent = Error.NotFound("agent.not_found", "You do not have an agent account.");
    public static readonly Error UnknownAgent = Error.NotFound("agent.unknown", "No active agent has this code.");
    public static readonly Error NotActive = Error.Conflict("agent.not_active", "Your agent account is not active.");
    public static readonly Error KycTooLow = Error.Forbidden("agent.kyc_required", "Verify your CNIC before applying to be an agent.");
    public static readonly Error InvalidShopName = Error.Validation("agent.invalid_shop_name", $"The shop name is {AgentRules.MinShopNameLength} to {AgentRules.MaxShopNameLength} characters.");
    public static readonly Error InvalidCity = Error.Validation("agent.invalid_city", $"Enter the city (at most {AgentRules.MaxPlaceLength} characters).");
    public static readonly Error InvalidNumber = Error.Validation("agent.invalid_number", "Enter the customer's mobile number, for example 0300 1234567.");
    public static readonly Error InvalidCashAmount = Error.Validation("agent.invalid_amount", "Cash-in and cash-out are Rs. 100 to Rs. 50,000, in whole rupees.");
    public static readonly Error InvalidFloatAmount = Error.Validation("agent.invalid_amount", "A float move is Rs. 100 to Rs. 1,000,000.");
    public static readonly Error InvalidDirection = Error.Validation("agent.invalid_direction", "The direction is \"ToFloat\" or \"ToWallet\".");
    public static readonly Error InvalidThreshold = Error.Validation("agent.invalid_threshold", "The alert level is Rs. 0 to Rs. 1,000,000.");
    public static readonly Error OwnNumber = Error.Validation("agent.own_wallet", "Use 'Move float' for your own wallet.");
    public static readonly Error NoRequest = Error.NotFound("agent.no_cash_out_request", "This customer has no open cash-out request at your shop. Ask them to create one in the app.");
    public static readonly Error CodeLocked = Error.Conflict("agent.code_locked", "Too many wrong codes. The customer has to create a new request.");
    public static readonly Error CodeTaken = Error.Conflict("agent.code_used", "This request is already being paid out.");
    public static readonly Error TransactionNotFound = Error.NotFound("agent.transaction_not_found", "No such transaction.");
    public static readonly Error RequestNotFound = Error.NotFound("agent.request_not_found", "No such cash-out request.");
    public static readonly Error CannotCancel = Error.Conflict("agent.cannot_cancel", "This request can no longer be cancelled.");
    public static readonly Error WrongStatus = Error.Conflict("agent.wrong_status", "The agent is not in a state that allows this.");

    public static Error WrongCode(int triesLeft) =>
        Error.Validation("agent.wrong_code", $"Wrong code. {triesLeft} {(triesLeft == 1 ? "try" : "tries")} left.");
}
