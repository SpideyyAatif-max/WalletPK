namespace WalletPK.Payments.Domain;

/// <summary>Wallet-to-wallet transfers (M6.1): states of the P2P Saga.</summary>
public static class P2PTransferStates
{
    /// <summary>Asked the Ledger to move the money.</summary>
    public const string Sending = "Sending";

    public const string Completed = "Completed";

    public const string Failed = "Failed";

    /// <summary>The Ledger did not answer for too long: an operator checks the journal.</summary>
    public const string NeedsReview = "NeedsReview";
}

/// <summary>Money requests (M6.2): states of the request Saga, which are also what both people see.</summary>
public static class MoneyRequestStates
{
    /// <summary>Waiting for the payer.</summary>
    public const string Pending = "Pending";

    /// <summary>The payer said yes; the Ledger is moving the money.</summary>
    public const string Paying = "Paying";

    public const string Paid = "Paid";

    public const string Declined = "Declined";

    public const string Cancelled = "Cancelled";

    public const string Expired = "Expired";

    public const string NeedsReview = "NeedsReview";
}

public static class WalletTransferRules
{
    /// <summary>Rs. 1 (the Ledger's minimum).</summary>
    public const long MinAmountPaisa = 100;

    /// <summary>Rs. 1,000,000 for one transfer; the customer's own limits are checked by the Ledger.</summary>
    public const long MaxAmountPaisa = 100_000_000;

    public const int MaxNoteLength = 140;

    /// <summary>A request waits this long for its payer unless the requester says otherwise.</summary>
    public const int DefaultRequestHours = 168;

    public const int MaxRequestHours = 720;

    public const int MaxSplitParticipants = 20;

    public const int MaxSplitTitleLength = 80;
}
