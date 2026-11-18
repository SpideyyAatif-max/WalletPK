namespace WalletPK.Bills.Domain;

/// <summary>A biller WalletPK collects for.</summary>
/// <param name="Code">"KE", "LESCO", "SNGPL", "PTCL" (also its circuit breaker and bulkhead).</param>
/// <param name="Name">Display name.</param>
/// <param name="Category">Electricity, Gas, Telephone &amp; Internet.</param>
/// <param name="ConsumerNumberLength">Digits in a consumer (reference) number.</param>
/// <param name="ConsumerNumberLabel">What the bill calls it.</param>
public sealed record Biller(string Code, string Name, string Category, int ConsumerNumberLength, string ConsumerNumberLabel);

/// <summary>The biller catalog (M7.1) and its consumer-number rules, checked before any biller is called.</summary>
public static class BillerCatalog
{
    public static IReadOnlyList<Biller> All { get; } =
    [
        new("KE", "K-Electric", "Electricity", 13, "Account number"),
        new("LESCO", "LESCO", "Electricity", 14, "Reference number"),
        new("SNGPL", "SNGPL", "Gas", 11, "Consumer number"),
        new("PTCL", "PTCL", "Telephone & Internet", 11, "Account ID"),
    ];

    public static Biller? ByCode(string? code) =>
        code is null ? null : All.FirstOrDefault(biller => biller.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The consumer number without spaces or dashes, or null when it is not one of this biller's.</summary>
    public static string? NormalizeConsumerNumber(Biller biller, string? input)
    {
        ArgumentNullException.ThrowIfNull(biller);
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = input.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return digits.Length == biller.ConsumerNumberLength && digits.All(char.IsAsciiDigit) ? digits : null;
    }
}

/// <summary>What the customer sees.</summary>
public static class BillPaymentStatus
{
    public const string Processing = "Processing";

    /// <summary>The biller has not confirmed either way yet; the money stays reserved until it does.</summary>
    public const string PendingVerification = "PendingVerification";

    /// <summary>Our team is resolving it (dead-lettered); the money stays reserved.</summary>
    public const string UnderReview = "UnderReview";

    public const string Completed = "Completed";

    /// <summary>Not paid; the reserved money is back in the wallet.</summary>
    public const string Failed = "Failed";
}

/// <summary>The bill payment Saga's states.</summary>
public static class BillPaymentStates
{
    /// <summary>Asked the Ledger to reserve the money.</summary>
    public const string Reserving = "Reserving";

    /// <summary>Paying the biller.</summary>
    public const string Paying = "Paying";

    /// <summary>The biller is unavailable; paying again shortly (it certainly has not been paid).</summary>
    public const string Retrying = "Retrying";

    /// <summary>The payment's outcome is unknown (timeout): asking the biller before paying again.</summary>
    public const string Verifying = "Verifying";

    /// <summary>Still no answer after the quick checks (M7.4): checking now and then until the biller answers.</summary>
    public const string PendingVerification = "PendingVerification";

    /// <summary>The biller has it; asked the Ledger to settle the reserved money to the biller settlement account.</summary>
    public const string Settling = "Settling";

    /// <summary>Not paid; asked the Ledger to return the reserved money.</summary>
    public const string Returning = "Returning";

    /// <summary>Needs a person (M7.5): replay, refund or confirm from the operations endpoints.</summary>
    public const string DeadLettered = "DeadLettered";

    public const string Completed = "Completed";

    public const string Failed = "Failed";
}

/// <summary>Limits of a bill payment and of saved billers.</summary>
public static class BillRules
{
    public const long MinAmountPaisa = 100;

    /// <summary>Rs. 1,000,000 for one bill; the customer's own limits are checked by the Ledger.</summary>
    public const long MaxAmountPaisa = 100_000_000;

    public const int MaxSavedBillers = 20;

    public const int MaxNicknameLength = 40;
}

/// <summary>When to remind a customer of a saved bill (M7.6).</summary>
public static class BillReminderRule
{
    /// <summary>
    /// Remind when this month's bill is unpaid and due within <paramref name="daysBefore"/> days (today,
    /// or already overdue included), once per billing month.
    /// </summary>
    /// <param name="status">The bill's status (Unpaid, Paid).</param>
    /// <param name="dueDate">Its due date (Pakistan).</param>
    /// <param name="billingMonth">"2026-11".</param>
    /// <param name="today">Today in Pakistan.</param>
    /// <param name="remindedForMonth">The billing month the customer was last reminded of.</param>
    /// <param name="daysBefore">How early.</param>
    public static bool ShouldRemind(string status, DateOnly dueDate, string billingMonth, DateOnly today, string? remindedForMonth, int daysBefore) =>
        status == "Unpaid"
        && remindedForMonth != billingMonth
        && dueDate.DayNumber - today.DayNumber <= daysBefore;
}
