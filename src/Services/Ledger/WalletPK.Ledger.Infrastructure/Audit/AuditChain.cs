using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WalletPK.Ledger.Infrastructure.Audit;

/// <summary>
/// The hash function of the audit chain:
/// <c>hash(n) = SHA-256( fields of row n, each length-prefixed | hash(n-1) )</c>.
/// Changing, removing or reordering any sealed row changes its hash, which no longer matches the
/// <c>previous_hash</c> stored in the next row, so the verifier finds the exact point of tampering.
/// Rewriting the whole tail is caught by comparing with head hashes recorded outside the database
/// (every seal logs the new head to Seq).
/// </summary>
public static class AuditChain
{
    /// <summary>The "previous hash" of the first row.</summary>
    public static readonly string GenesisHash = new('0', 64);

    public static string ComputeHash(AuditRecord record, long chainSequence, string previousHash)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousHash);

        // Length-prefixing makes the encoding unambiguous: no choice of field values can collide with another.
        var canonical = new StringBuilder(256 + record.Payload.Length);
        Append(canonical, "v1");
        Append(canonical, chainSequence.ToString(CultureInfo.InvariantCulture));
        Append(canonical, record.Id.ToString(CultureInfo.InvariantCulture));
        Append(canonical, ((record.OccurredAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond).ToString(CultureInfo.InvariantCulture));
        Append(canonical, record.EventType);
        Append(canonical, record.SubjectType);
        Append(canonical, record.SubjectId);
        Append(canonical, record.Actor);
        Append(canonical, record.CorrelationId ?? string.Empty);
        Append(canonical, record.Payload);
        Append(canonical, previousHash);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
}
