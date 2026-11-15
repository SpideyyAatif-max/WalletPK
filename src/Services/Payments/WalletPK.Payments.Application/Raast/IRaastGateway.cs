using WalletPK.Payments.Domain;

namespace WalletPK.Payments.Application.Raast;

/// <summary>How a call to Raast ended, as far as a Saga is concerned.</summary>
public enum RaastCallOutcome
{
    /// <summary>Raast answered.</summary>
    Ok,

    /// <summary>Raast refused the request (4xx): sending it again will not help.</summary>
    Rejected,

    /// <summary>Raast does not know it (404): for a status query, the transfer never arrived.</summary>
    NotFound,

    /// <summary>
    /// Certainly not done: the bank is down (503), its circuit is open, or its bulkhead is full. Safe to
    /// send again later.
    /// </summary>
    Unavailable,

    /// <summary>No answer (timeout, dropped connection): it may or may not have been done. Ask before resending.</summary>
    Unknown,
}

/// <summary>An account as Raast's title fetch or alias lookup shows it.</summary>
public sealed record RaastAccount(string Iban, string BankCode, string BankName, string Title, string Status);

public sealed record RaastLookupResult(RaastCallOutcome Outcome, RaastAccount? Account, string? Code, string? Detail);

/// <param name="TransferId">WalletPK's ID for the transfer (the Saga ID); Raast treats a repeat as the same transfer.</param>
/// <param name="AmountPaisa">How much.</param>
/// <param name="ToIban">The beneficiary's IBAN.</param>
/// <param name="Bank">The beneficiary's bank (whose circuit breaker and bulkhead the call goes through).</param>
/// <param name="SenderName">Shown to the beneficiary.</param>
/// <param name="Purpose">The customer's note.</param>
public sealed record RaastSendRequest(Guid TransferId, long AmountPaisa, string ToIban, RaastBank Bank, string? SenderName, string? Purpose);

public sealed record RaastSendResult(RaastCallOutcome Outcome, string? RaastReference, string? Code, string? Detail);

/// <summary>Raast's statuses for a transfer.</summary>
public static class RaastTransferStatus
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Rejected = "Rejected";
}

/// <param name="Outcome">Whether Raast answered.</param>
/// <param name="Status">When it did: Pending, Completed or Rejected.</param>
/// <param name="RaastReference">Raast's reference.</param>
/// <param name="Reason">Why it was rejected (account_closed, account_blocked ...).</param>
/// <param name="Detail">What went wrong with the call, when it did.</param>
public sealed record RaastStatusResult(RaastCallOutcome Outcome, string? Status, string? RaastReference, string? Reason, string? Detail);

/// <summary>
/// Raast, as Payments uses it. Every call goes through the bank's resilience pipeline (bulkhead,
/// timeouts, retries with jitter, circuit breaker); none of them throws for a failed call: the outcome
/// says what is known.
/// </summary>
public interface IRaastGateway
{
    /// <summary>Raast ID lookup: the account a mobile number points to.</summary>
    Task<RaastLookupResult> LookupAliasAsync(string mobileE164, CancellationToken cancellationToken);

    /// <summary>Title fetch: the account holder's name and the account's status.</summary>
    Task<RaastLookupResult> FetchTitleAsync(string iban, RaastBank bank, CancellationToken cancellationToken);

    Task<RaastSendResult> SendAsync(RaastSendRequest request, CancellationToken cancellationToken);

    Task<RaastStatusResult> GetStatusAsync(Guid transferId, RaastBank bank, CancellationToken cancellationToken);
}

/// <summary>A bank's health as its circuit breaker sees it.</summary>
/// <param name="Code">The bank's code.</param>
/// <param name="Name">Its name.</param>
/// <param name="Circuit">Closed, Open or HalfOpen.</param>
/// <param name="Status">Available, Degraded (recovering: trial calls only) or Down (transfers are queued).</param>
/// <param name="Since">When the circuit last changed state.</param>
public sealed record BankAvailability(string Code, string Name, string Circuit, string Status, DateTimeOffset? Since);

public interface IBankAvailability
{
    IReadOnlyList<BankAvailability> GetAll();
}
