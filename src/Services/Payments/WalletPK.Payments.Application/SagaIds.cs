using System.Security.Cryptography;
using System.Text;

namespace WalletPK.Payments.Application;

/// <summary>
/// Saga and transfer IDs derived from what identifies a request (a user and their Idempotency-Key, a
/// request and an attempt number ...): the same inputs always give the same ID, so a retry finds the
/// Saga it already started, and the Ledger sees the same journal ID.
/// </summary>
public static class SagaIds
{
    public static Guid From(string purpose, params object[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(parts);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"walletpk:payments:{purpose}:{string.Join(':', parts)}"), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8 (custom)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }
}
