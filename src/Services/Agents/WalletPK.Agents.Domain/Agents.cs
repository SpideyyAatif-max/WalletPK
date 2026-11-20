using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WalletPK.Agents.Domain;

/// <summary>An agent's life: applied, approved (wallet being opened), active; or rejected, suspended.</summary>
public static class AgentStatus
{
    /// <summary>Applied; operations have to approve it.</summary>
    public const string PendingApproval = "PendingApproval";

    /// <summary>Approved; the Ledger is opening its float.</summary>
    public const string Opening = "Opening";

    public const string Active = "Active";

    public const string Suspended = "Suspended";

    public const string Rejected = "Rejected";
}

/// <summary>What an agent does: cash-in and cash-out for customers, and float moves for itself.</summary>
public static class AgentTransactionKind
{
    /// <summary>The agent takes cash; the customer's wallet is credited from the float.</summary>
    public const string CashIn = "CashIn";

    /// <summary>The customer's wallet pays the float; the agent hands over cash.</summary>
    public const string CashOut = "CashOut";

    /// <summary>From the agent's own wallet to its float.</summary>
    public const string FloatIn = "FloatIn";

    /// <summary>From the float to the agent's own wallet.</summary>
    public const string FloatOut = "FloatOut";
}

public static class AgentRules
{
    public const int MinShopNameLength = 3;

    public const int MaxShopNameLength = 60;

    public const int MaxPlaceLength = 40;

    /// <summary>Cash-in and cash-out: Rs. 100 to Rs. 50,000, whole rupees (it is cash).</summary>
    public const long MinCashPaisa = 10_000;

    public const long MaxCashPaisa = 5_000_000;

    /// <summary>Float moves: Rs. 100 to Rs. 1,000,000.</summary>
    public const long MinFloatMovePaisa = 10_000;

    public const long MaxFloatMovePaisa = 100_000_000;

    /// <summary>The float alert level unless the agent sets one: Rs. 5,000.</summary>
    public const long DefaultLowFloatPaisa = 500_000;

    public static bool IsValidCash(long paisa) => paisa is >= MinCashPaisa and <= MaxCashPaisa && paisa % 100 == 0;

    public static bool IsValidFloatMove(long paisa) => paisa is >= MinFloatMovePaisa and <= MaxFloatMovePaisa;
}

/// <param name="AmountPaisa">Cash handed over.</param>
/// <param name="FeePaisa">WalletPK's fee, paid by the customer (cash-out only).</param>
/// <param name="TaxPaisa">Tax on the fee.</param>
/// <param name="CommissionPaisa">The agent's commission, accrued and paid later.</param>
/// <param name="TotalPaisa">What leaves the customer's wallet (cash-out) or arrives in it (cash-in).</param>
public sealed record AgentQuote(long AmountPaisa, long FeePaisa, long TaxPaisa, long CommissionPaisa, long TotalPaisa);

/// <summary>
/// The commission plan (M9.4). A cash-in is free for the customer and WalletPK pays the agent a commission;
/// a cash-out costs the customer a fee (plus 16% tax on it) and the agent earns a share of the fee.
/// Commissions are paid to the float every night, less withholding tax.
/// </summary>
public static class CommissionPlan
{
    /// <summary>Cash-in commission: 0.4% (min Rs. 2, max Rs. 40).</summary>
    public const int CashInBasisPoints = 40;

    public const long CashInMinPaisa = 200;

    public const long CashInMaxPaisa = 4_000;

    /// <summary>Cash-out fee: 1% (min Rs. 10, max Rs. 300).</summary>
    public const int CashOutFeeBasisPoints = 100;

    public const long CashOutFeeMinPaisa = 1_000;

    public const long CashOutFeeMaxPaisa = 30_000;

    /// <summary>The agent's share of the cash-out fee: 60%.</summary>
    public const int AgentShareBasisPoints = 6_000;

    /// <summary>Sales tax on fees: 16%.</summary>
    public const int TaxBasisPoints = 1_600;

    /// <summary>Withholding tax on commissions paid out: 12%.</summary>
    public const int WithholdingBasisPoints = 1_200;

    public static AgentQuote CashIn(long amountPaisa) =>
        new(amountPaisa, 0, 0, Math.Clamp(Round(amountPaisa * CashInBasisPoints), CashInMinPaisa, CashInMaxPaisa), amountPaisa);

    public static AgentQuote CashOut(long amountPaisa)
    {
        var fee = Math.Clamp(Round(amountPaisa * CashOutFeeBasisPoints), CashOutFeeMinPaisa, CashOutFeeMaxPaisa);
        var tax = Round(fee * TaxBasisPoints);
        return new AgentQuote(amountPaisa, fee, tax, Round(fee * AgentShareBasisPoints), amountPaisa + fee + tax);
    }

    /// <summary>Withholding tax on a commission payout of <paramref name="grossPaisa"/>.</summary>
    public static long Withholding(long grossPaisa) => Round(grossPaisa * WithholdingBasisPoints);

    /// <summary>Half up, in whole paisa, of <paramref name="numerator"/> / 10,000.</summary>
    private static long Round(long numerator) => (numerator + 5_000) / 10_000;
}

/// <summary>
/// One-time cash-out codes (M9.3): the customer asks for cash at an agent in the app and gets a six-digit
/// code; the agent types it to take the money. Only a keyed hash of the code is stored.
/// </summary>
public static class CashOutCodes
{
    public const int Digits = 6;

    /// <summary>A code lasts 15 minutes.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Wrong codes before the request is locked.</summary>
    public const int MaxAttempts = 5;

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>HMAC-SHA256 of the request and its code, hex: the code cannot be read back from it, or reused on another request.</summary>
    public static string Hash(Guid requestId, string code, byte[] key) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{requestId:N}:{code}")));

    public static bool Matches(Guid requestId, string? code, string hash, byte[] key) =>
        code is { Length: Digits }
        && code.All(char.IsAsciiDigit)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(requestId, code, key)), Encoding.ASCII.GetBytes(hash));
}
