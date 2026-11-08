using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WalletPK.Web.Security;

/// <summary>
/// HMAC SHA-256 signatures for webhooks (callbacks from banks, billers and telcos).
/// The sender signs "<c>{unix-timestamp}.{raw body}</c>" with a shared secret and sends:
/// <code>
/// X-WalletPK-Timestamp: 1794135411
/// X-WalletPK-Signature: sha256=5f2b...
/// </code>
/// Signing the timestamp together with the body means an attacker can neither change the body
/// nor replay an old request with a fresh timestamp.
/// </summary>
public static class WebhookSignature
{
    public const string SignatureHeader = "X-WalletPK-Signature";
    public const string TimestampHeader = "X-WalletPK-Timestamp";
    public const string SchemePrefix = "sha256=";

    /// <summary>Computes the signature header value for a payload.</summary>
    public static string Compute(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var signedData = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signedData, 0);
        body.CopyTo(signedData.AsSpan(prefix.Length));

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signedData);
        return SchemePrefix + Convert.ToHexStringLower(hash);
    }

    /// <summary>Checks a received signature in constant time (no timing leak of how many characters matched).</summary>
    public static bool Verify(string secret, long timestamp, ReadOnlySpan<byte> body, string? signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var expected = Compute(secret, timestamp, body);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature));
    }
}
