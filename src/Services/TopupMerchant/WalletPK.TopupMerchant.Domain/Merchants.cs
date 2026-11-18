using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WalletPK.TopupMerchant.Domain;

/// <summary>A fee plan (M8.2): a percentage of each payment, with a floor and a ceiling.</summary>
/// <param name="Code">"STANDARD", "KIRYANA" ...</param>
/// <param name="Name">For people.</param>
/// <param name="BasisPoints">The rate in hundredths of a percent (100 = 1%).</param>
/// <param name="MinFeePaisa">Smallest fee.</param>
/// <param name="MaxFeePaisa">Largest fee.</param>
public sealed record FeePlan(string Code, string Name, int BasisPoints, long MinFeePaisa, long MaxFeePaisa)
{
    /// <summary>Sales tax on the fee (16%).</summary>
    public const int TaxBasisPoints = 1_600;

    /// <summary>The fee and its tax for a payment of <paramref name="amountPaisa"/>; the merchant gets the rest.</summary>
    public FeeSplit Split(long amountPaisa)
    {
        var fee = Math.Clamp(Round(amountPaisa * BasisPoints, 10_000), MinFeePaisa, MaxFeePaisa);
        var tax = Round(fee * TaxBasisPoints, 10_000);
        return new FeeSplit(amountPaisa, fee, tax, amountPaisa - fee - tax);
    }

    /// <summary>Half up, in whole paisa.</summary>
    private static long Round(long numerator, long denominator) => (numerator + (denominator / 2)) / denominator;
}

/// <param name="AmountPaisa">What the customer pays.</param>
/// <param name="FeePaisa">WalletPK's fee, paid by the merchant.</param>
/// <param name="TaxPaisa">Tax on the fee.</param>
/// <param name="NetPaisa">What the merchant gets.</param>
public sealed record FeeSplit(long AmountPaisa, long FeePaisa, long TaxPaisa, long NetPaisa);

/// <summary>A kind of business, with the fee plan it starts on.</summary>
public sealed record MerchantCategory(string Code, string Name, string FeePlan);

public static class FeePlans
{
    public static IReadOnlyList<FeePlan> All { get; } =
    [
        new("STANDARD", "Standard", 100, 100, 50_000),
        new("KIRYANA", "Kiryana & small shops", 50, 0, 25_000),
        new("FOOD", "Food & restaurants", 80, 100, 40_000),
        new("FUEL", "Fuel stations", 30, 100, 20_000),
    ];

    public static IReadOnlyList<MerchantCategory> Categories { get; } =
    [
        new("RETAIL", "Retail", "STANDARD"),
        new("KIRYANA", "Kiryana store", "KIRYANA"),
        new("FOOD", "Restaurant / food", "FOOD"),
        new("FUEL", "Fuel station", "FUEL"),
        new("SERVICES", "Services", "STANDARD"),
    ];

    public static FeePlan? ByCode(string? code) => All.FirstOrDefault(plan => plan.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    public static MerchantCategory? Category(string? code) => Categories.FirstOrDefault(category => category.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}

public static class MerchantRules
{
    public const int MaxNameLength = 60;

    public const int MaxReferenceLength = 40;

    /// <summary>A QR code is good for 1 to 15 minutes (5 by default).</summary>
    public const int MinQrSeconds = 60;

    public const int MaxQrSeconds = 900;

    public const int DefaultQrSeconds = 300;

    /// <summary>Rs. 10 ... Rs. 500,000 per QR payment (from Rs. 10 every fee plan leaves the merchant something).</summary>
    public const long MinAmountPaisa = 1_000;

    public const long MaxAmountPaisa = 50_000_000;
}

/// <summary>What a dynamic QR code says (M8.3): who to pay, how much, until when, and which code it is.</summary>
/// <param name="QrId">The code (paid once).</param>
/// <param name="MerchantCode">"WPKM123456".</param>
/// <param name="MerchantName">Shown to the payer.</param>
/// <param name="AmountPaisa">Exactly this.</param>
/// <param name="Reference">The merchant's reference ("INV-42").</param>
/// <param name="ExpiresAt">Not payable after this.</param>
public sealed record QrContent(Guid QrId, string MerchantCode, string MerchantName, long AmountPaisa, string? Reference, DateTimeOffset ExpiresAt);

/// <summary>
/// Signed QR payloads: <c>WPKQR1.{base64url JSON}.{base64url HMAC-SHA256}</c>. Anyone can read one, nobody
/// can change the amount or the merchant without the key, and the server checks expiry and single use.
/// </summary>
public static class QrCodec
{
    public const string Prefix = "WPKQR1";

    public static string Encode(QrContent content, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(key);
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Wire(content.QrId, content.MerchantCode, content.MerchantName, content.AmountPaisa, "PKR", content.Reference, content.ExpiresAt.ToUnixTimeSeconds()), JsonSerializerOptions.Web));
        return $"{Prefix}.{body}.{Base64Url(Sign(body, key))}";
    }

    /// <summary>The content, or null when the payload is malformed or its signature does not match.</summary>
    public static QrContent? Decode(string? payload, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var parts = payload?.Trim().Split('.');
        if (parts is not [Prefix, var body, var signature] || body.Length > 2_000)
        {
            return null;
        }

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(FromBase64Url(signature), Sign(body, key)))
            {
                return null;
            }

            var wire = JsonSerializer.Deserialize<Wire>(FromBase64Url(body), JsonSerializerOptions.Web);
            return wire is { M: { Length: > 0 }, N: { Length: > 0 }, C: "PKR" }
                ? new QrContent(wire.Q, wire.M, wire.N, wire.A, wire.R, DateTimeOffset.FromUnixTimeSeconds(wire.E))
                : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private static byte[] Sign(string body, byte[] key) => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{Prefix}.{body}"));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    /// <summary>Short property names keep the QR small.</summary>
    private sealed record Wire(Guid Q, string M, string N, long A, string C, string? R, long E);
}
