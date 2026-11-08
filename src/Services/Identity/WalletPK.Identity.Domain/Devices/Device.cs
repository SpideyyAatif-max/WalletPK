using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;

namespace WalletPK.Identity.Domain.Devices;

/// <summary>
/// A phone the user has proved they own (MPIN + an OTP sent to their number) and may use without
/// another OTP. Device binding is what stops a stolen MPIN alone from opening the wallet elsewhere.
/// <para>
/// A device added while the user already has another one starts a <b>cooling-off period</b>
/// (24 hours by default): it can log in, but high-risk actions such as large transfers are limited
/// until the period ends. This blunts SIM-swap fraud, where an attacker gets the OTP on a new phone.
/// </para>
/// </summary>
public sealed class Device : AggregateRoot<DeviceId>
{
    public const int InstallationIdMaxLength = 128;
    public const int NameMaxLength = 64;

    private Device(DeviceId id, UserId userId, string installationId, string name, DateTimeOffset now, DateTimeOffset? coolingOffUntil)
        : base(id)
    {
        UserId = userId;
        InstallationId = installationId;
        Name = name;
        Status = DeviceStatus.Trusted;
        TrustedAt = now;
        LastSeenAt = now;
        CoolingOffUntil = coolingOffUntil;
    }

    /// <summary>For EF Core materialisation only.</summary>
    private Device()
    {
    }

    public UserId UserId { get; private set; }

    /// <summary>Random ID the app generates once per installation and keeps in secure storage.</summary>
    public string InstallationId { get; private set; } = null!;

    /// <summary>Shown to the user in their device list, for example "Samsung Galaxy A54".</summary>
    public string Name { get; private set; } = null!;

    public DeviceStatus Status { get; private set; }

    public DateTimeOffset TrustedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>End of the new-device cooling-off period; null if there is none.</summary>
    public DateTimeOffset? CoolingOffUntil { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Optimistic concurrency token (PostgreSQL xmin).</summary>
    public uint Version { get; private set; }

    public bool IsTrusted => Status == DeviceStatus.Trusted;

    /// <summary>Trusts a device after OTP verification, with an optional cooling-off period.</summary>
    public static Device Trust(UserId userId, string installationId, string name, DateTimeOffset now, TimeSpan? coolingOff)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var device = new Device(DeviceId.New(), userId, installationId, name, now, now + coolingOff);
        device.Raise(new DeviceTrusted(device.Id, userId, now, device.CoolingOffUntil));
        return device;
    }

    /// <summary>Trusts a previously revoked device again (after a fresh OTP), restarting any cooling-off.</summary>
    public void TrustAgain(string name, DateTimeOffset now, TimeSpan? coolingOff)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        Status = DeviceStatus.Trusted;
        TrustedAt = now;
        LastSeenAt = now;
        RevokedAt = null;
        CoolingOffUntil = now + coolingOff;
        Raise(new DeviceTrusted(Id, UserId, now, CoolingOffUntil));
    }

    /// <summary>The end of the cooling-off period if it is still running, otherwise null.</summary>
    public DateTimeOffset? ActiveCoolingOffUntil(DateTimeOffset now) =>
        CoolingOffUntil is { } until && until > now ? until : null;

    public void MarkSeen(DateTimeOffset now) => LastSeenAt = now;

    /// <summary>Stops trusting the device. Safe to call more than once.</summary>
    public void Revoke(DateTimeOffset now)
    {
        if (Status == DeviceStatus.Revoked)
        {
            return;
        }

        Status = DeviceStatus.Revoked;
        RevokedAt = now;
        Raise(new DeviceRevoked(Id, UserId, now));
    }
}
