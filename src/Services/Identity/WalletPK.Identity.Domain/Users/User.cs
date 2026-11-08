using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;

namespace WalletPK.Identity.Domain.Users;

/// <summary>
/// A WalletPK user. Created only after the phone number has been verified by OTP and an MPIN
/// has been chosen, so the users table never holds unverified sign-ups.
/// </summary>
public sealed class User : AggregateRoot<UserId>
{
    private User(UserId id, PhoneNumber phoneNumber, string mpinHash, DateTimeOffset now)
        : base(id)
    {
        PhoneNumber = phoneNumber;
        MpinHash = mpinHash;
        Status = UserStatus.Active;
        KycTier = KycTier.L0;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>For EF Core materialisation only.</summary>
    private User()
    {
    }

    public PhoneNumber PhoneNumber { get; private set; } = null!;

    /// <summary>Argon2id hash of the MPIN. The MPIN itself is never stored.</summary>
    public string MpinHash { get; private set; } = null!;

    public UserStatus Status { get; private set; }

    public KycTier KycTier { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token (PostgreSQL xmin): concurrent updates cannot silently overwrite each other.</summary>
    public uint Version { get; private set; }

    /// <summary>Creates a verified user with a hashed MPIN, at the lowest KYC tier.</summary>
    public static User Register(PhoneNumber phoneNumber, string mpinHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(phoneNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(mpinHash);

        var user = new User(UserId.New(), phoneNumber, mpinHash, now);
        user.Raise(new UserRegistered(user.Id, phoneNumber.Value, now));
        return user;
    }
}
