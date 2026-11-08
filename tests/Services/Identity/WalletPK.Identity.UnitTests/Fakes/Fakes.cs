using System.Text.RegularExpressions;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Sessions;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.UnitTests.Fakes;

internal sealed class InMemoryOtpStore : IOtpStore
{
    private readonly Dictionary<(OtpPurpose, string), OtpRecord> _codes = new();
    private readonly HashSet<(OtpPurpose, string)> _cooldowns = new();

    public TimeSpan? TimeToLive { get; private set; }

    public void ClearCooldowns() => _cooldowns.Clear();

    public OtpRecord? Peek(OtpPurpose purpose, PhoneNumber phone) =>
        _codes.TryGetValue((purpose, phone.Value), out var record) ? record : null;

    public Task<TimeSpan?> TryBeginCooldownAsync(OtpPurpose purpose, PhoneNumber phoneNumber, TimeSpan cooldown, CancellationToken cancellationToken) =>
        Task.FromResult(_cooldowns.Add((purpose, phoneNumber.Value)) ? (TimeSpan?)null : TimeSpan.FromSeconds(12));

    public Task SaveAsync(OtpPurpose purpose, PhoneNumber phoneNumber, string codeHash, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        _codes[(purpose, phoneNumber.Value)] = new OtpRecord(codeHash, 0);
        TimeToLive = timeToLive;
        return Task.CompletedTask;
    }

    public Task<OtpRecord?> GetAsync(OtpPurpose purpose, PhoneNumber phoneNumber, CancellationToken cancellationToken) =>
        Task.FromResult(Peek(purpose, phoneNumber));

    public Task<int> IncrementFailedAttemptsAsync(OtpPurpose purpose, PhoneNumber phoneNumber, CancellationToken cancellationToken)
    {
        var key = (purpose, phoneNumber.Value);
        var updated = _codes[key] with { FailedAttempts = _codes[key].FailedAttempts + 1 };
        _codes[key] = updated;
        return Task.FromResult(updated.FailedAttempts);
    }

    public Task DeleteAsync(OtpPurpose purpose, PhoneNumber phoneNumber, CancellationToken cancellationToken)
    {
        _codes.Remove((purpose, phoneNumber.Value));
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryRegistrationTokenStore : IRegistrationTokenStore
{
    private readonly Dictionary<string, PhoneNumber> _tokens = new(StringComparer.Ordinal);

    public bool Contains(string token) => _tokens.ContainsKey(token);

    public Task SaveAsync(string token, PhoneNumber phoneNumber, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        _tokens[token] = phoneNumber;
        return Task.CompletedTask;
    }

    public Task<PhoneNumber?> ConsumeAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult(_tokens.Remove(token, out var phone) ? phone : null);
}

/// <summary>Users repository that is also the unit of work for all in-memory repositories.</summary>
internal sealed class InMemoryUsers : IUserRepository, IUnitOfWork
{
    public List<User> Saved { get; } = [];

    public List<User> Pending { get; } = [];

    public bool ThrowUniqueViolationOnSave { get; set; }

    public bool ThrowConcurrencyConflictOnSave { get; set; }

    public int SaveCount { get; private set; }

    public Task<bool> ExistsByPhoneAsync(PhoneNumber phoneNumber, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.Any(user => user.PhoneNumber == phoneNumber));

    public Task<User?> GetByPhoneAsync(PhoneNumber phoneNumber, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.FirstOrDefault(user => user.PhoneNumber == phoneNumber));

    public Task<User?> GetByIdAsync(UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.FirstOrDefault(user => user.Id == userId));

    public void Add(User user) => Pending.Add(user);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (ThrowUniqueViolationOnSave)
        {
            throw new UniqueConstraintViolationException("duplicate") { ConstraintName = "ux_users_phone_number" };
        }

        if (ThrowConcurrencyConflictOnSave)
        {
            throw new ConcurrencyConflictException("changed");
        }

        Saved.AddRange(Pending);
        Pending.Clear();
        SaveCount++;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryDevices : IDeviceRepository
{
    public List<Device> All { get; } = [];

    public Task<Device?> GetByIdAsync(DeviceId deviceId, CancellationToken cancellationToken) =>
        Task.FromResult(All.FirstOrDefault(device => device.Id == deviceId));

    public Task<Device?> GetByInstallationAsync(UserId userId, string installationId, CancellationToken cancellationToken) =>
        Task.FromResult(All.FirstOrDefault(device => device.UserId == userId && device.InstallationId == installationId));

    public Task<bool> HasOtherDevicesAsync(UserId userId, string installationId, CancellationToken cancellationToken) =>
        Task.FromResult(All.Any(device => device.UserId == userId && device.InstallationId != installationId));

    public Task<IReadOnlyList<Device>> ListByUserAsync(UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Device>>([.. All.Where(device => device.UserId == userId).OrderByDescending(device => device.LastSeenAt)]);

    public void Add(Device device) => All.Add(device);
}

internal sealed class InMemoryRefreshTokens : IRefreshTokenRepository
{
    public List<RefreshToken> All { get; } = [];

    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(All.FirstOrDefault(token => token.TokenHash == tokenHash));

    public Task<IReadOnlyList<RefreshToken>> ListByFamilyAsync(Guid familyId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RefreshToken>>([.. All.Where(token => token.FamilyId == familyId)]);

    public Task<IReadOnlyList<RefreshToken>> ListUnusedByDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RefreshToken>>([.. All.Where(token => token.DeviceId == deviceId && !token.IsConsumed && !token.IsRevoked)]);

    public void Add(RefreshToken refreshToken) => All.Add(refreshToken);
}

internal sealed class InMemoryDeviceChallenges : IDeviceChallengeStore
{
    private readonly Dictionary<string, DeviceChallenge> _challenges = new(StringComparer.Ordinal);

    public int Count => _challenges.Count;

    public Task SaveAsync(string challengeId, DeviceChallenge challenge, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        _challenges[challengeId] = challenge;
        return Task.CompletedTask;
    }

    public Task<DeviceChallenge?> GetAsync(string challengeId, CancellationToken cancellationToken) =>
        Task.FromResult(_challenges.TryGetValue(challengeId, out var challenge) ? challenge : null);

    public Task DeleteAsync(string challengeId, CancellationToken cancellationToken)
    {
        _challenges.Remove(challengeId);
        return Task.CompletedTask;
    }
}

/// <summary>Records what it was asked to put in the token instead of signing a real JWT.</summary>
internal sealed class FakeAccessTokenIssuer : IAccessTokenIssuer
{
    public List<AccessTokenClaims> Issued { get; } = [];

    public IssuedAccessToken Issue(AccessTokenClaims claims, DateTimeOffset now)
    {
        Issued.Add(claims);
        return new IssuedAccessToken($"access-{Issued.Count}", now.AddMinutes(15));
    }
}

internal sealed partial class CapturingSmsSender : ISmsSender
{
    public List<(string To, string Message)> Messages { get; } = [];

    public Task SendAsync(PhoneNumber to, string message, CancellationToken cancellationToken)
    {
        Messages.Add((to.Value, message));
        return Task.CompletedTask;
    }

    public string LastCode() => SixDigits().Match(Messages[^1].Message).Value;

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex SixDigits();
}

internal sealed class FakeMpinHasher : IMpinHasher
{
    public int HashCalls { get; private set; }

    public string Hash(string mpin)
    {
        HashCalls++;
        return "hashed:" + mpin;
    }

    public bool Verify(string mpin, string hash) => hash == "hashed:" + mpin;
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>A clock tests can move forward.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public void Advance(TimeSpan by) => Now += by;

    public override DateTimeOffset GetUtcNow() => Now;
}
