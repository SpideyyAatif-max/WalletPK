using WalletPK.Payments.Domain;

namespace WalletPK.Payments.Application.BankTransfers;

/// <summary>A Raast transfer's data, stored as JSON in its saga_instances row.</summary>
public sealed class BankTransferData
{
    public Guid UserId { get; set; }

    /// <summary>The customer's Idempotency-Key (the Saga ID is derived from it).</summary>
    public string IdempotencyKey { get; set; } = "";

    public long AmountPaisa { get; set; }

    public string ToIban { get; set; } = "";

    /// <summary>The bank's code ("MCB").</summary>
    public string ToBank { get; set; } = "";

    public string? BeneficiaryTitle { get; set; }

    public string? Note { get; set; }

    public string? DeviceId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>What the customer sees (<see cref="BankTransferStatus"/>).</summary>
    public string Status { get; set; } = BankTransferStatus.Processing;

    public string? RaastReference { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>Calls to Raast's send API (including retries after an outage).</summary>
    public int SendAttempts { get; set; }

    /// <summary>When the bank was first found unavailable (the queue's clock).</summary>
    public DateTimeOffset? QueuedSince { get; set; }

    /// <summary>When the current wait for an answer (verifying or awaiting the bank) began.</summary>
    public DateTimeOffset? WaitingSince { get; set; }

    /// <summary>Status queries in the current wait.</summary>
    public int StatusChecks { get; set; }

    /// <summary>Times the current Ledger command was sent again because no reply came.</summary>
    public int LedgerResends { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Raast's confirmation callback (from the webhook), as the Saga receives it.</summary>
/// <param name="TransferId">The transfer (Saga) ID.</param>
/// <param name="RaastReference">Raast's reference.</param>
/// <param name="Status">Completed or Rejected.</param>
/// <param name="Reason">Why it was rejected.</param>
public sealed record RaastTransferCallback(Guid TransferId, string RaastReference, string Status, string? Reason);

/// <summary>Bank transfer settings, from "Payments:BankTransfers".</summary>
public sealed class BankTransferOptions
{
    public const string SectionName = "Payments:BankTransfers";

    /// <summary>The reserved money returns to the wallet by itself after this long (Ledger maximum: 7 days).</summary>
    public int HoldLifetimeHours { get; set; } = 168;

    /// <summary>A Ledger command with no reply is sent again after this long.</summary>
    public int LedgerReplyTimeoutSeconds { get; set; } = 60;

    /// <summary>After this many unanswered resends the transfer is given up (or, when settling, reviewed).</summary>
    public int MaxLedgerResends { get; set; } = 60;

    /// <summary>First retry after the bank was found unavailable; doubles up to <see cref="QueueRetryMaxSeconds"/>.</summary>
    public int QueueRetryInitialSeconds { get; set; } = 15;

    public int QueueRetryMaxSeconds { get; set; } = 300;

    /// <summary>A queued transfer is given up (money returned) after this long.</summary>
    public int MaxQueueHours { get; set; } = 24;

    /// <summary>First status query after a send with no clear answer.</summary>
    public int VerifyDelaySeconds { get; set; } = 5;

    /// <summary>First status query after Raast accepted it; doubles up to <see cref="StatusCheckMaxSeconds"/>.</summary>
    public int StatusCheckInitialSeconds { get; set; } = 30;

    public int StatusCheckMaxSeconds { get; set; } = 600;

    /// <summary>With no final answer after this long, a transfer goes to review (an operator decides).</summary>
    public int MaxWaitHours { get; set; } = 48;

    /// <summary>Shown to beneficiaries as the sender.</summary>
    public string SenderName { get; set; } = "WalletPK";

    public bool IsValid() =>
        HoldLifetimeHours is >= 1 and <= 168
        && LedgerReplyTimeoutSeconds >= 1
        && MaxLedgerResends >= 1
        && QueueRetryInitialSeconds >= 1
        && QueueRetryMaxSeconds >= QueueRetryInitialSeconds
        && MaxQueueHours >= 1
        && VerifyDelaySeconds >= 1
        && StatusCheckInitialSeconds >= 1
        && StatusCheckMaxSeconds >= StatusCheckInitialSeconds
        && MaxWaitHours >= 1
        // The reserved money must outlive the longest the transfer can still be sent and confirmed.
        && HoldLifetimeHours > MaxQueueHours + MaxWaitHours
        && SenderName.Length is > 0 and <= 70;
}
