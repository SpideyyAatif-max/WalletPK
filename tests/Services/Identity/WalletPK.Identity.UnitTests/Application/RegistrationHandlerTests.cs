using WalletPK.Common.Results;
using WalletPK.Identity.Application.Otp;
using WalletPK.Identity.Application.Registration;
using WalletPK.Identity.Domain.Users;
using WalletPK.Identity.UnitTests.Fakes;

namespace WalletPK.Identity.UnitTests.Application;

public class RegistrationHandlerTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private readonly InMemoryOtpStore _otpStore = new();
    private readonly CapturingSmsSender _sms = new();
    private readonly InMemoryRegistrationTokenStore _tokens = new();
    private readonly InMemoryUsers _users = new();
    private readonly OtpService _otp;

    public RegistrationHandlerTests()
    {
        _otp = new OtpService(_otpStore, _sms, new OtpOptions { Pepper = "unit-test-pepper-that-is-at-least-32-chars" });
    }

    private StartRegistrationHandler Start() => new(_users, _otp);

    private VerifyRegistrationOtpHandler Verify() => new(_otp, _tokens, new RegistrationOptions());

    private CompleteRegistrationHandler Complete() =>
        new(_tokens, _users, _users, new FakeMpinHasher(), new FixedTimeProvider(_now));

    private async Task<string> GetRegistrationTokenAsync(string phone)
    {
        await Start().HandleAsync(new StartRegistrationCommand(phone), Cancel);
        var ticket = await Verify().HandleAsync(new VerifyRegistrationOtpCommand(phone, _sms.LastCode()), Cancel);
        return ticket.Value.RegistrationToken;
    }

    [Fact]
    public async Task Start_rejects_invalid_numbers()
    {
        var result = await Start().HandleAsync(new StartRegistrationCommand("12345"), Cancel);

        result.Error.Code.ShouldBe("phone.invalid");
        _sms.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_sends_an_otp_and_returns_a_masked_number()
    {
        var result = await Start().HandleAsync(new StartRegistrationCommand("0300 1234567"), Cancel);

        result.Value.PhoneNumber.ShouldBe("+92300*****67");
        result.Value.ExpiresInSeconds.ShouldBe(120);
        _sms.Messages.ShouldHaveSingleItem().To.ShouldBe("+923001234567");
    }

    [Fact]
    public async Task Verifying_the_code_returns_a_long_random_token()
    {
        var token = await GetRegistrationTokenAsync("03001234567");

        token.Length.ShouldBe(64);
        _tokens.Contains(token).ShouldBeTrue();
    }

    [Fact]
    public async Task Completing_creates_an_active_l0_user_with_a_hashed_mpin()
    {
        var token = await GetRegistrationTokenAsync("03001234567");

        var result = await Complete().HandleAsync(new CompleteRegistrationCommand(token, "40291", "40291"), Cancel);

        result.Value.KycTier.ShouldBe(KycTier.L0);
        result.Value.PhoneNumber.ShouldBe("+92300*****67");
        result.Value.RegisteredAt.ShouldBe(_now);
        var user = _users.Saved.ShouldHaveSingleItem();
        user.MpinHash.ShouldBe("hashed:40291");
        _tokens.Contains(token).ShouldBeFalse();
    }

    [Fact]
    public async Task A_weak_or_mismatched_mpin_keeps_the_token_so_the_user_can_retry()
    {
        var token = await GetRegistrationTokenAsync("03001234567");

        var mismatch = await Complete().HandleAsync(new CompleteRegistrationCommand(token, "40291", "40292"), Cancel);
        var weak = await Complete().HandleAsync(new CompleteRegistrationCommand(token, "12345", "12345"), Cancel);
        var retry = await Complete().HandleAsync(new CompleteRegistrationCommand(token, "40291", "40291"), Cancel);

        mismatch.Error.Code.ShouldBe("mpin.mismatch");
        weak.Error.Code.ShouldBe("mpin.too_simple");
        retry.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public async Task Unknown_or_used_tokens_cannot_create_accounts(string? token)
    {
        var result = await Complete().HandleAsync(new CompleteRegistrationCommand(token, "40291", "40291"), Cancel);

        result.Error.Code.ShouldBe("registration.session_expired");
        _users.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_registered_number_cannot_register_again()
    {
        var token = await GetRegistrationTokenAsync("03001234567");
        await Complete().HandleAsync(new CompleteRegistrationCommand(token, "40291", "40291"), Cancel);
        _otpStore.ClearCooldowns();

        var again = await Start().HandleAsync(new StartRegistrationCommand("03001234567"), Cancel);

        again.Error.Code.ShouldBe("identity.phone_already_registered");
        again.Error.Type.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public async Task A_database_unique_violation_becomes_a_conflict()
    {
        var token = await GetRegistrationTokenAsync("03001234567");
        _users.ThrowUniqueViolationOnSave = true;

        var result = await Complete().HandleAsync(new CompleteRegistrationCommand(token, "40291", "40291"), Cancel);

        result.Error.Code.ShouldBe("identity.phone_already_registered");
    }
}
