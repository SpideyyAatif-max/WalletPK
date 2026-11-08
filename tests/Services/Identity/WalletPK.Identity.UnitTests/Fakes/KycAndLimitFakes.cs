using WalletPK.Common.Identifiers;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Kyc;

namespace WalletPK.Identity.UnitTests.Fakes;

internal sealed class InMemoryKycProfiles : IKycProfileRepository
{
    public List<KycProfile> All { get; } = [];

    public Task<KycProfile?> GetByUserIdAsync(UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult(All.FirstOrDefault(profile => profile.Id == userId));

    public Task<bool> ExistsByCnicLookupAsync(string cnicLookupHash, CancellationToken cancellationToken) =>
        Task.FromResult(All.Any(profile => profile.CnicLookupHash == cnicLookupHash));

    public void Add(KycProfile profile) => All.Add(profile);
}

internal sealed class FakeNadra : INadraClient
{
    public NadraOutcome Outcome { get; set; } = NadraOutcome.Verified;

    public int Calls { get; private set; }

    public Task<NadraVerification> VerifyAsync(Cnic cnic, string fullName, DateOnly dateOfBirth, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new NadraVerification(Outcome, Outcome == NadraOutcome.Verified ? "NADRA-TEST-1" : null));
    }
}

/// <summary>Same rules as the Redis Lua scripts, in memory.</summary>
internal sealed class InMemoryLimitUsageStore : ILimitUsageStore
{
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Amount, string Day, string Month)> _reservations = new(StringComparer.Ordinal);

    public Task<LimitUsage> GetUsageAsync(UserId userId, DateOnly businessDate, CancellationToken cancellationToken) =>
        Task.FromResult(Usage(DayKey(userId, businessDate), MonthKey(userId, businessDate)));

    public Task<ReservationOutcome> TryReserveAsync(LimitReservation reservation, CancellationToken cancellationToken)
    {
        var day = DayKey(reservation.UserId, reservation.BusinessDate);
        var month = MonthKey(reservation.UserId, reservation.BusinessDate);
        var usage = Usage(day, month);

        if (_reservations.ContainsKey(reservation.TransactionId))
        {
            return Task.FromResult(new ReservationOutcome(ReservationStatus.AlreadyReserved, usage));
        }

        if (usage.DailyUsedPaisa + reservation.AmountPaisa > reservation.DailyLimitPaisa)
        {
            return Task.FromResult(new ReservationOutcome(ReservationStatus.ExceedsDaily, usage));
        }

        if (usage.MonthlyUsedPaisa + reservation.AmountPaisa > reservation.MonthlyLimitPaisa)
        {
            return Task.FromResult(new ReservationOutcome(ReservationStatus.ExceedsMonthly, usage));
        }

        _counters[day] = usage.DailyUsedPaisa + reservation.AmountPaisa;
        _counters[month] = usage.MonthlyUsedPaisa + reservation.AmountPaisa;
        _reservations[reservation.TransactionId] = (reservation.AmountPaisa, day, month);
        return Task.FromResult(new ReservationOutcome(ReservationStatus.Reserved, Usage(day, month)));
    }

    public Task<bool> ReleaseAsync(string transactionId, CancellationToken cancellationToken)
    {
        if (!_reservations.Remove(transactionId, out var reservation))
        {
            return Task.FromResult(false);
        }

        _counters[reservation.Day] -= reservation.Amount;
        _counters[reservation.Month] -= reservation.Amount;
        return Task.FromResult(true);
    }

    private LimitUsage Usage(string day, string month) =>
        new(_counters.GetValueOrDefault(day), _counters.GetValueOrDefault(month));

    private static string DayKey(UserId userId, DateOnly date) => $"{userId}:{date:yyyyMMdd}";

    private static string MonthKey(UserId userId, DateOnly date) => $"{userId}:{date:yyyyMM}";
}
