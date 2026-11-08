using System.Globalization;
using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Results;

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
        Role = UserRole.Customer;
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

    public UserRole Role { get; private set; }

    /// <summary>Wrong MPINs in a row since the last successful login or lockout.</summary>
    public int FailedMpinAttempts { get; private set; }

    /// <summary>When a temporary lockout ends; null when the account is not locked.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

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

    /// <summary>
    /// Checks whether the user may attempt to log in now. A lockout that has run out is lifted here,
    /// so no background job is needed to unlock accounts.
    /// </summary>
    public Result EnsureCanLogIn(DateTimeOffset now)
    {
        if (Status == UserStatus.Frozen)
        {
            return UserErrors.Frozen;
        }

        if (Status == UserStatus.Locked)
        {
            if (LockedUntil is { } until && until > now)
            {
                var minutes = Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes));
                return Error.Forbidden(
                    UserErrors.LockedCode,
                    string.Create(CultureInfo.InvariantCulture, $"Too many incorrect MPINs. Try again in {minutes} minute(s)."));
            }

            Status = UserStatus.Active;
            LockedUntil = null;
            FailedMpinAttempts = 0;
            UpdatedAt = now;
        }

        return Result.Success();
    }

    /// <summary>
    /// Counts a wrong MPIN. When the limit is reached the account is locked for <paramref name="lockoutDuration"/>
    /// and the method returns true.
    /// </summary>
    public bool RecordFailedMpin(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        FailedMpinAttempts++;
        UpdatedAt = now;

        if (FailedMpinAttempts < maxAttempts)
        {
            return false;
        }

        Status = UserStatus.Locked;
        LockedUntil = now + lockoutDuration;
        FailedMpinAttempts = 0;
        Raise(new UserLockedOut(Id, now, LockedUntil.Value));
        return true;
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedMpinAttempts = 0;
        LastLoginAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Whether money may leave this account now. Frozen accounts never; a temporarily locked account
    /// (someone was guessing the MPIN) not until the lockout ends.
    /// </summary>
    public bool CanTransact(DateTimeOffset now) =>
        Status switch
        {
            UserStatus.Active => true,
            UserStatus.Locked => LockedUntil is not { } until || until <= now,
            _ => false,
        };

    /// <summary>Raises the KYC tier after verification. Tiers only go up; asking for the same or a lower tier fails.</summary>
    public Result UpgradeKycTier(KycTier newTier, DateTimeOffset now)
    {
        if (newTier <= KycTier)
        {
            return UserErrors.AlreadyAtTier(KycTier);
        }

        var previous = KycTier;
        KycTier = newTier;
        UpdatedAt = now;
        Raise(new KycUpgraded(Id, previous, newTier, now));
        return Result.Success();
    }
}
