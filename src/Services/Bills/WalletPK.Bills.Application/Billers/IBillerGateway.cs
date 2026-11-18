using WalletPK.Bills.Domain;

namespace WalletPK.Bills.Application.Billers;

/// <summary>How a call to a biller ended (see WalletPK.Resilience's ProviderOutcome).</summary>
public enum BillerCallOutcome
{
    Ok,

    /// <summary>Refused (4xx): already paid, disconnected, wrong amount ... Trying again will not help.</summary>
    Rejected,

    /// <summary>Not known to the biller: no such consumer, or (status query) the payment never arrived.</summary>
    NotFound,

    /// <summary>Certainly not done: the biller is down, its circuit is open, or its bulkhead is full.</summary>
    Unavailable,

    /// <summary>No answer: it may or may not have been done. Ask before paying again.</summary>
    Unknown,
}

/// <summary>This month's bill, as the biller reports it (M7.2).</summary>
public sealed record BillInfo(
    string Biller,
    string ConsumerNumber,
    string CustomerName,
    string BillingMonth,
    DateOnly DueDate,
    long AmountWithinDuePaisa,
    long AmountAfterDuePaisa,
    long PayablePaisa,
    string Status);

public sealed record BillInquiryResult(BillerCallOutcome Outcome, BillInfo? Bill, string? Code, string? Detail);

/// <summary>A payment the biller holds.</summary>
public sealed record BillerReceipt(string BillerReference, string BillingMonth, long AmountPaisa, DateTimeOffset PaidAt);

public sealed record BillerPaymentResult(BillerCallOutcome Outcome, BillerReceipt? Receipt, string? Code, string? Detail);

/// <summary>
/// The billers, as Bills uses them. Every call goes through the biller's own resilience pipeline; none
/// throws for a failed call.
/// </summary>
public interface IBillerGateway
{
    Task<BillInquiryResult> InquireAsync(Biller biller, string consumerNumber, CancellationToken cancellationToken);

    /// <summary>Pays this month's bill. <paramref name="paymentId"/> makes a repeat the same payment.</summary>
    Task<BillerPaymentResult> PayAsync(Biller biller, Guid paymentId, string consumerNumber, long amountPaisa, CancellationToken cancellationToken);

    /// <summary>The status query: did payment <paramref name="paymentId"/> arrive?</summary>
    Task<BillerPaymentResult> GetPaymentAsync(Biller biller, Guid paymentId, CancellationToken cancellationToken);
}

/// <summary>A biller's health as its circuit breaker sees it.</summary>
public sealed record BillerAvailability(string Code, string Name, string Category, int ConsumerNumberLength, string ConsumerNumberLabel, string Status, DateTimeOffset? Since);

public interface IBillerAvailability
{
    IReadOnlyList<BillerAvailability> GetAll();
}
