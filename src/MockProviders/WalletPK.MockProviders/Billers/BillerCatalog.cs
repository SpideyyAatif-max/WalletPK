using WalletPK.MockProviders.Common;

namespace WalletPK.MockProviders.Billers;

/// <summary>A biller WalletPK can pay.</summary>
/// <param name="Code">"KE", "LESCO", "SNGPL", "PTCL".</param>
/// <param name="Name">Display name.</param>
/// <param name="Category">Electricity, Gas, Telephone.</param>
/// <param name="ConsumerNumberLength">How many digits a consumer (reference) number has.</param>
/// <param name="MinBillRupees">Smallest bill it issues.</param>
/// <param name="MaxBillRupees">Largest bill it issues.</param>
public sealed record Biller(string Code, string Name, string Category, int ConsumerNumberLength, long MinBillRupees, long MaxBillRupees);

/// <summary>One month's bill.</summary>
/// <param name="Biller">The biller's code.</param>
/// <param name="ConsumerNumber">The consumer (reference) number.</param>
/// <param name="CustomerName">The account holder.</param>
/// <param name="BillingMonth">"2026-11".</param>
/// <param name="DueDate">Pay by this date (Pakistan time) to pay the lower amount.</param>
/// <param name="AmountWithinDuePaisa">Payable up to the due date.</param>
/// <param name="AmountAfterDuePaisa">Payable after it (10% late payment surcharge).</param>
/// <param name="PayablePaisa">What has to be paid today.</param>
/// <param name="Status">Unpaid or Paid.</param>
public sealed record Bill(
    string Biller,
    string ConsumerNumber,
    string CustomerName,
    string BillingMonth,
    DateOnly DueDate,
    long AmountWithinDuePaisa,
    long AmountAfterDuePaisa,
    long PayablePaisa,
    string Status);

/// <summary>
/// The mock billers and their bills (M5.3). Bills are derived from the biller, consumer number and
/// month, so every valid consumer number has a bill and it never changes within the month. Each bill is
/// issued on the 1st and due on the 20th (Pakistan time). Test values:
/// <list type="table">
/// <item><term>consumer number ends in 0000</term><description>no such consumer (404)</description></item>
/// <item><term>ends in 1111</term><description>this month's bill is already paid</description></item>
/// <item><term>ends in 2222</term><description>connection disconnected: payments are refused (422)</description></item>
/// </list>
/// </summary>
public static class BillerCatalog
{
    public static IReadOnlyList<Biller> All { get; } =
    [
        new("KE", "K-Electric", "Electricity", 13, 1_500, 45_000),
        new("LESCO", "Lahore Electric Supply Company", "Electricity", 14, 1_200, 40_000),
        new("SNGPL", "Sui Northern Gas Pipelines", "Gas", 11, 400, 12_000),
        new("PTCL", "Pakistan Telecommunication Company", "Telephone & Internet", 11, 1_100, 4_800),
    ];

    public const int DueDay = 20;

    public static Biller? ByCode(string code) => All.FirstOrDefault(biller => biller.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    public static bool IsValidConsumerNumber(Biller biller, string consumerNumber) =>
        consumerNumber.Length == biller.ConsumerNumberLength && consumerNumber.All(char.IsAsciiDigit);

    public static bool NotFound(string consumerNumber) => consumerNumber.EndsWith("0000", StringComparison.Ordinal);

    public static bool PrePaid(string consumerNumber) => consumerNumber.EndsWith("1111", StringComparison.Ordinal);

    public static bool Disconnected(string consumerNumber) => consumerNumber.EndsWith("2222", StringComparison.Ordinal);

    /// <summary>The bill for the month containing <paramref name="today"/> (a Pakistan date).</summary>
    public static Bill BillFor(Biller biller, string consumerNumber, DateOnly today, bool paid)
    {
        ArgumentNullException.ThrowIfNull(biller);
        var month = $"{today:yyyy-MM}";
        var rupees = Deterministic.Between(biller.MinBillRupees, biller.MaxBillRupees, "bill", biller.Code, consumerNumber, month);
        var withinDue = rupees * 100;
        var afterDue = (long)Math.Round(rupees * 1.1m, MidpointRounding.AwayFromZero) * 100;
        var dueDate = new DateOnly(today.Year, today.Month, DueDay);
        return new Bill(
            biller.Code,
            consumerNumber,
            PakistaniNames.For("bill-holder", biller.Code, consumerNumber),
            month,
            dueDate,
            withinDue,
            afterDue,
            today <= dueDate ? withinDue : afterDue,
            paid ? "Paid" : "Unpaid");
    }
}
