using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Devices;

namespace WalletPK.Identity.Domain.Sessions;

/// <summary>
/// A long-lived credential that buys new short-lived access tokens. Only its SHA-256 hash is stored.
/// <para>
/// <b>Rotation:</b> every refresh consumes the token and issues a new one in the same <see cref="FamilyId"/>.
/// <b>Reuse detection:</b> if an already-consumed token is presented again, someone copied it, so the
/// whole family (the attacker's and the real user's sessions) is revoked and the user must log in again.
/// </para>
/// </summary>
public sealed class RefreshToken : Entity<Guid>
{
    private RefreshToken(Guid id, UserId userId, DeviceId deviceId, Guid familyId, string tokenHash, DateTimeOffset now, DateTimeOffset expiresAt)
        : base(id)
    {
        UserId = userId;
        DeviceId = deviceId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    /// <summary>For EF Core materialisation only.</summary>
    private RefreshToken()
    {
    }

    public UserId UserId { get; private set; }

    public DeviceId DeviceId { get; private set; }

    /// <summary>All tokens descended from one login share a family.</summary>
    public Guid FamilyId { get; private set; }

    /// <summary>Hex SHA-256 of the token. The token has 256 random bits, so a plain hash is enough.</summary>
    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the token was exchanged for a new one.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>When the token was cancelled (logout, device removal or detected reuse).</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Optimistic concurrency token (PostgreSQL xmin): two refreshes with one token cannot both win.</summary>
    public uint Version { get; private set; }

    public bool IsConsumed => ConsumedAt is not null;

    public bool IsRevoked => RevokedAt is not null;

    public static RefreshToken Issue(UserId userId, DeviceId deviceId, Guid familyId, string tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        return new RefreshToken(Guid.CreateVersion7(), userId, deviceId, familyId, tokenHash, now, now + lifetime);
    }

    public bool IsActive(DateTimeOffset now) => ConsumedAt is null && RevokedAt is null && ExpiresAt > now;

    public void Consume(DateTimeOffset now)
    {
        if (!IsActive(now))
        {
            throw new InvalidOperationException("Only an active refresh token can be consumed.");
        }

        ConsumedAt = now;
    }

    /// <summary>Cancels the token. Safe to call more than once.</summary>
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
