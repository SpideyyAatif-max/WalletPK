using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace WalletPK.Identity.Application.Authentication;

/// <summary>Creates refresh tokens and the hashes stored for them.</summary>
public static class RefreshTokenSecret
{
    /// <summary>256 random bits, URL-safe Base64 (43 characters).</summary>
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Hex SHA-256. A fast hash is right here (unlike for MPINs): the token is 256 random bits,
    /// so it cannot be guessed, and a stolen database holds no usable tokens.
    /// </summary>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
