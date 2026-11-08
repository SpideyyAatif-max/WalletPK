using System.Security.Cryptography;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Kyc;
using WalletPK.Identity.Application.Limits;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Users;
using WalletPK.Identity.Infrastructure.Security;
using WalletPK.Identity.UnitTests.Fakes;

namespace WalletPK.Identity.UnitTests.Application;

public class KycAndLimitHandlerTests
{
    private const string Cnic = "35202-1234567-1";
    private static readonly DateOnly _adultBirthday = new(1998, 4, 21);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // 10:00 UTC = 15:00 in Pakistan, 8 November 2026.
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
    private readonly InMemoryUsers _users = new();
    private readonly InMemoryDevices _devices = new();
    private readonly InMemoryKycProfiles _profiles = new();
    private readonly InMemoryLimitUsageStore _usage = new();
    private readonly FakeNadra _nadra = new();
    private readonly AesGcmFieldEncryptor _encryptor;
    private readonly HmacBlindIndex _blindIndex;
    private readonly User _user;

    public KycAndLimitHandlerTests()
    {
        var keys = new EncryptionOptions
        {
            Keys = { ["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) },
            ActiveKeyId = "k1",
            BlindIndexKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };
        _encryptor = new AesGcmFieldEncryptor(keys);
        _blindIndex = new HmacBlindIndex(keys);
        _user = AddUser("03001234567");
    }

    private User AddUser(string phone)
    {
        var user = User.Register(PhoneNumber.Create(phone).Value, "hash", _clock.Now);
        _users.Saved.Add(user);
        return user;
    }

    private SubmitKycHandler Kyc() => new(_users, _profiles, _users, _nadra, _encryptor, _blindIndex, _clock);

    private LimitService Limits() => new(_users, _devices, _usage, new LimitsOptions(), _clock);

    private static LimitRequest Debit(User user, decimal rupees, DeviceId? device = null) =>
        new(user.Id, Money.FromRupees(rupees), DebitType.P2PTransfer, device);

    // ---------------- KYC ----------------

    [Fact]
    public async Task A_verified_cnic_upgrades_the_user_to_l1_and_is_stored_encrypted()
    {
        var result = await Kyc().HandleAsync(_user.Id, new SubmitKycCommand(Cnic, "  Ayesha   Khan ", _adultBirthday), Cancel);

        result.Value.KycTier.ShouldBe(KycTier.L1);
        result.Value.CnicMasked.ShouldBe("35202-*******-1");
        result.Value.FullName.ShouldBe("Ayesha Khan");
        _user.KycTier.ShouldBe(KycTier.L1);
        _user.DomainEvents.OfType<KycUpgraded>().ShouldHaveSingleItem();

        var profile = _profiles.All.ShouldHaveSingleItem();
        profile.CnicCiphertext.ShouldNotContain("3520212345671");
        profile.CnicCiphertext.ShouldStartWith("v1.k1.");
        _encryptor.Decrypt(profile.CnicCiphertext, _user.Id.ToString()).ShouldBe("3520212345671");
        profile.CnicLookupHash.ShouldBe(_blindIndex.Compute("3520212345671"));
        profile.VerificationReference.ShouldBe("NADRA-TEST-1");
    }

    [Fact]
    public async Task The_same_cnic_cannot_verify_a_second_account()
    {
        var other = AddUser("03111234567");
        await Kyc().HandleAsync(_user.Id, new SubmitKycCommand(Cnic, "Ayesha Khan", _adultBirthday), Cancel);

        var second = await Kyc().HandleAsync(other.Id, new SubmitKycCommand("3520212345671", "Ayesha Khan", _adultBirthday), Cancel);

        second.Error.ShouldBe(KycErrors.CnicInUse);
        other.KycTier.ShouldBe(KycTier.L0);
        _nadra.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(NadraOutcome.NotFound, "kyc.not_verified")]
    [InlineData(NadraOutcome.Mismatch, "kyc.not_verified")]
    [InlineData(NadraOutcome.Unavailable, "kyc.nadra_unavailable")]
    public async Task A_failed_nadra_check_changes_nothing(NadraOutcome outcome, string expectedCode)
    {
        _nadra.Outcome = outcome;

        var result = await Kyc().HandleAsync(_user.Id, new SubmitKycCommand(Cnic, "Ayesha Khan", _adultBirthday), Cancel);

        result.Error.Code.ShouldBe(expectedCode);
        _user.KycTier.ShouldBe(KycTier.L0);
        _profiles.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bad_input_is_rejected_before_nadra_is_called()
    {
        var underage = await Kyc().HandleAsync(_user.Id, new SubmitKycCommand(Cnic, "Ayesha Khan", new DateOnly(2010, 1, 1)), Cancel);
        var badName = await Kyc().HandleAsync(_user.Id, new SubmitKycCommand(Cnic, "A1", _adultBirthday), Cancel);
        var noBirthday = await Kyc().HandleAsync(_user.Id, new SubmitKycCommand(Cnic, "Ayesha Khan", null), Cancel);
        var badCnic = await Kyc().HandleAsync(_user.Id, new SubmitKycCommand("123", "Ayesha Khan", _adultBirthday), Cancel);

        underage.Error.ShouldBe(KycErrors.Underage);
        badName.Error.ShouldBe(KycErrors.NameInvalid);
        noBirthday.Error.ShouldBe(KycErrors.DateOfBirthRequired);
        badCnic.Error.Code.ShouldBe("cnic.invalid");
        _nadra.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task An_already_verified_user_cannot_submit_again()
    {
        await Kyc().HandleAsync(_user.Id, new SubmitKycCommand(Cnic, "Ayesha Khan", _adultBirthday), Cancel);

        var again = await Kyc().HandleAsync(_user.Id, new SubmitKycCommand("42101-7654321-2", "Ayesha Khan", _adultBirthday), Cancel);

        again.Error.Code.ShouldBe("kyc.already_verified");
    }

    // ---------------- Limits ----------------

    [Fact]
    public async Task An_l0_user_is_limited_per_transaction_and_per_day()
    {
        var single = await Limits().CheckAsync(Debit(_user, 10_001), Cancel);
        var first = await Limits().ReserveAsync("txn-1", Debit(_user, 10_000), Cancel);
        var second = await Limits().ReserveAsync("txn-2", Debit(_user, 10_000), Cancel);
        var third = await Limits().ReserveAsync("txn-3", Debit(_user, 10_000), Cancel);

        single.Value.Decision.ShouldBe(LimitDecision.ExceedsPerTransaction);
        first.Value.Decision.ShouldBe(LimitDecision.Allowed);
        second.Value.DailyRemaining.ShouldBe(Money.FromRupees(5_000));
        third.Value.Decision.ShouldBe(LimitDecision.ExceedsDaily);
        third.Value.Reason.ShouldContain("Rs. 5,000 more today");
    }

    [Fact]
    public async Task Reserving_is_idempotent_and_releasing_gives_the_limit_back()
    {
        await Limits().ReserveAsync("txn-1", Debit(_user, 10_000), Cancel);
        var retry = await Limits().ReserveAsync("txn-1", Debit(_user, 10_000), Cancel);
        retry.Value.DailyRemaining.ShouldBe(Money.FromRupees(15_000));

        (await Limits().ReleaseAsync("txn-1", Cancel)).ShouldBeTrue();
        (await Limits().ReleaseAsync("txn-1", Cancel)).ShouldBeFalse();

        var summary = await Limits().GetSummaryAsync(_user.Id, null, Cancel);
        summary.Value.Daily.Used.ShouldBe(Money.Zero());
        summary.Value.Daily.Remaining.ShouldBe(Money.FromRupees(25_000));
    }

    [Fact]
    public async Task A_device_in_cooling_off_may_send_at_most_5000_at_a_time()
    {
        var newPhone = Device.Trust(_user.Id, "install-new-phone-0001", "New phone", _clock.Now, TimeSpan.FromHours(24));
        _devices.Add(newPhone);

        var big = await Limits().CheckAsync(Debit(_user, 6_000, newPhone.Id), Cancel);
        var small = await Limits().CheckAsync(Debit(_user, 5_000, newPhone.Id), Cancel);
        var withoutDevice = await Limits().CheckAsync(Debit(_user, 6_000), Cancel);

        big.Value.Decision.ShouldBe(LimitDecision.CoolingOff);
        big.Value.PerTransactionLimit.ShouldBe(Money.FromRupees(5_000));
        small.Value.IsAllowed.ShouldBeTrue();
        withoutDevice.Value.IsAllowed.ShouldBeTrue();

        _clock.Advance(TimeSpan.FromHours(24));
        (await Limits().CheckAsync(Debit(_user, 6_000, newPhone.Id), Cancel)).Value.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Kyc_raises_the_limits()
    {
        await Kyc().HandleAsync(_user.Id, new SubmitKycCommand(Cnic, "Ayesha Khan", _adultBirthday), Cancel);

        var result = await Limits().CheckAsync(Debit(_user, 40_000), Cancel);

        result.Value.IsAllowed.ShouldBeTrue();
        result.Value.KycTier.ShouldBe(KycTier.L1);
        result.Value.PerTransactionLimit.ShouldBe(Money.FromRupees(50_000));
    }

    [Fact]
    public async Task Locked_or_unknown_users_and_bad_amounts_are_refused()
    {
        for (var i = 0; i < 5; i++)
        {
            _user.RecordFailedMpin(_clock.Now, 5, TimeSpan.FromMinutes(30));
        }

        var locked = await Limits().CheckAsync(Debit(_user, 100), Cancel);
        var unknown = await Limits().CheckAsync(new LimitRequest(UserId.New(), Money.FromRupees(100), DebitType.BillPayment, null), Cancel);
        var zero = await Limits().CheckAsync(Debit(_user, 0), Cancel);
        var noTransactionId = await Limits().ReserveAsync(" ", Debit(_user, 100), Cancel);

        locked.Value.Decision.ShouldBe(LimitDecision.UserNotAllowed);
        unknown.Value.Decision.ShouldBe(LimitDecision.UserNotAllowed);
        zero.Error.ShouldBe(LimitService.AmountInvalid);
        noTransactionId.Error.ShouldBe(LimitService.TransactionIdRequired);
    }

    [Fact]
    public void Default_limits_are_consistent()
    {
        var options = new LimitsOptions();

        options.IsValid().ShouldBeTrue();
        options.For(KycTier.L0).DailyMoney.ShouldBe(Money.FromRupees(25_000));
        new LimitsOptions { L1 = new TierLimits { PerTransaction = 1, Daily = 1, Monthly = 1, MaxBalance = 1 } }.IsValid().ShouldBeFalse();
    }
}
