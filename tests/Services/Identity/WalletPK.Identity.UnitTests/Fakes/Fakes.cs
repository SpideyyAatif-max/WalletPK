using System.Text.RegularExpressions;
using WalletPK.Identity.Application.Abstractions;
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

internal sealed class InMemoryUsers : IUserRepository, IUnitOfWork
{
    public List<User> Saved { get; } = [];

    public List<User> Pending { get; } = [];

    public bool ThrowUniqueViolationOnSave { get; set; }

    public Task<bool> ExistsByPhoneAsync(PhoneNumber phoneNumber, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.Any(user => user.PhoneNumber == phoneNumber));

    public Task<User?> GetByPhoneAsync(PhoneNumber phoneNumber, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.FirstOrDefault(user => user.PhoneNumber == phoneNumber));

    public void Add(User user) => Pending.Add(user);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (ThrowUniqueViolationOnSave)
        {
            throw new UniqueConstraintViolationException("duplicate") { ConstraintName = "ux_users_phone_number" };
        }

        Saved.AddRange(Pending);
        Pending.Clear();
        return Task.CompletedTask;
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
    public string Hash(string mpin) => "hashed:" + mpin;

    public bool Verify(string mpin, string hash) => hash == "hashed:" + mpin;
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
