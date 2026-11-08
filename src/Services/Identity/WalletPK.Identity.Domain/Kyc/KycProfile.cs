using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;

namespace WalletPK.Identity.Domain.Kyc;

/// <summary>
/// The verified identity behind an account (one per user, created when NADRA confirms the CNIC).
/// <para>
/// The CNIC itself is stored only encrypted (AES-256-GCM). To enforce "one CNIC, one wallet"
/// without decrypting every row, a keyed hash of the CNIC (a <b>blind index</b>) is stored too,
/// with a unique index on it. Only the masked form is kept in clear text.
/// </para>
/// </summary>
public sealed class KycProfile : Entity<UserId>
{
    public const int FullNameMaxLength = 100;

    private KycProfile(
        UserId userId,
        string cnicCiphertext,
        string cnicLookupHash,
        string cnicMasked,
        string fullName,
        DateOnly dateOfBirth,
        string verificationReference,
        DateTimeOffset verifiedAt)
        : base(userId)
    {
        CnicCiphertext = cnicCiphertext;
        CnicLookupHash = cnicLookupHash;
        CnicMasked = cnicMasked;
        FullName = fullName;
        DateOfBirth = dateOfBirth;
        VerificationReference = verificationReference;
        VerifiedAt = verifiedAt;
    }

    /// <summary>For EF Core materialisation only.</summary>
    private KycProfile()
    {
    }

    /// <summary>The user this profile belongs to (also the primary key: one profile per user).</summary>
    public UserId UserId => Id;

    /// <summary>The CNIC encrypted with AES-256-GCM, bound to this user (it cannot be copied to another row).</summary>
    public string CnicCiphertext { get; private set; } = null!;

    /// <summary>HMAC-SHA256 of the CNIC with a secret key: finds duplicates without revealing the number.</summary>
    public string CnicLookupHash { get; private set; } = null!;

    /// <summary>For display, for example "35202-*******-1".</summary>
    public string CnicMasked { get; private set; } = null!;

    public string FullName { get; private set; } = null!;

    public DateOnly DateOfBirth { get; private set; }

    /// <summary>NADRA's reference for the successful verification (for audits and disputes).</summary>
    public string VerificationReference { get; private set; } = null!;

    public DateTimeOffset VerifiedAt { get; private set; }

    /// <summary>Optimistic concurrency token (PostgreSQL xmin).</summary>
    public uint Version { get; private set; }

    public static KycProfile CreateVerified(
        UserId userId,
        string cnicCiphertext,
        string cnicLookupHash,
        Cnic cnic,
        string fullName,
        DateOnly dateOfBirth,
        string verificationReference,
        DateTimeOffset verifiedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cnicCiphertext);
        ArgumentException.ThrowIfNullOrWhiteSpace(cnicLookupHash);
        ArgumentNullException.ThrowIfNull(cnic);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationReference);

        return new KycProfile(userId, cnicCiphertext, cnicLookupHash, cnic.Masked, fullName, dateOfBirth, verificationReference, verifiedAt);
    }
}
