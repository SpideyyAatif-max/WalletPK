namespace WalletPK.Payments.Domain;

/// <summary>What the customer sees: four statuses, whatever the Saga is doing inside.</summary>
public static class BankTransferStatus
{
    /// <summary>Accepted, money reserved or on its way.</summary>
    public const string Processing = "Processing";

    /// <summary>The bank is unavailable; WalletPK will send it as soon as the bank is back (money stays reserved).</summary>
    public const string Queued = "Queued";

    /// <summary>The money reached the beneficiary's bank.</summary>
    public const string Completed = "Completed";

    /// <summary>It did not go through; any reserved money was returned to the wallet.</summary>
    public const string Failed = "Failed";
}

/// <summary>The Raast transfer Saga's states (stored in saga_instances.state).</summary>
public static class BankTransferStates
{
    /// <summary>Asked the Ledger to reserve the money.</summary>
    public const string Reserving = "Reserving";

    /// <summary>Money reserved; sending to Raast.</summary>
    public const string Sending = "Sending";

    /// <summary>The bank is down (or its circuit is open); sending again later.</summary>
    public const string Queued = "Queued";

    /// <summary>The send's outcome is unknown (timeout, dropped connection): asking Raast what happened.</summary>
    public const string Verifying = "Verifying";

    /// <summary>Raast accepted it; waiting for the beneficiary bank.</summary>
    public const string AwaitingBank = "AwaitingBank";

    /// <summary>The bank credited the beneficiary; asked the Ledger to settle the reserved money.</summary>
    public const string Settling = "Settling";

    /// <summary>It will not go through; asked the Ledger to return the reserved money.</summary>
    public const string Returning = "Returning";

    /// <summary>Something only a person can resolve (an operator is alerted). Still accepts the bank's answer.</summary>
    public const string NeedsReview = "NeedsReview";

    public const string Completed = "Completed";

    public const string Failed = "Failed";
}

/// <summary>The limits of one Raast transfer.</summary>
public static class BankTransferRules
{
    /// <summary>Rs. 1.</summary>
    public const long MinAmountPaisa = 100;

    /// <summary>Rs. 1,000,000 (Raast's per-transfer limit; the customer's own limits are checked by the Ledger).</summary>
    public const long MaxAmountPaisa = 100_000_000;

    /// <summary>A note to the beneficiary.</summary>
    public const int MaxNoteLength = 140;

    public const int MaxTitleLength = 100;
}
