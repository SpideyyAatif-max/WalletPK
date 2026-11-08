using WalletPK.Common.Identifiers;
using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Account;
using WalletPK.Identity.Application.Authentication;
using WalletPK.Identity.Application.Otp;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Users;
using WalletPK.Identity.UnitTests.Fakes;

namespace WalletPK.Identity.UnitTests.Application;

public class AuthenticationHandlerTests
{
    private const string Phone = "0300 1234567";
    private const string Mpin = "40291";
    private const string Phone1 = "install-phone-one-0001";
    private const string Phone2 = "install-phone-two-0002";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
    private readonly InMemoryOtpStore _otpStore = new();
    private readonly CapturingSmsSender _sms = new();
    private readonly InMemoryUsers _users = new();
    private readonly InMemoryDevices _devices = new();
    private readonly InMemoryRefreshTokens _refreshTokens = new();
    private readonly InMemoryDeviceChallenges _challenges = new();
    private readonly FakeAccessTokenIssuer _accessTokens = new();
    private readonly FakeMpinHasher _hasher = new();
    private readonly AuthOptions _options = new();
    private readonly OtpService _otp;
    private readonly SessionIssuer _sessions;
    private readonly User _user;

    public AuthenticationHandlerTests()
    {
        _otp = new OtpService(_otpStore, _sms, new OtpOptions { Pepper = "unit-test-pepper-that-is-at-least-32-chars" });
        _sessions = new SessionIssuer(_accessTokens, _refreshTokens, _options);
        _user = User.Register(PhoneNumber.Create(Phone).Value, _hasher.Hash(Mpin), _clock.Now);
        _users.Saved.Add(_user);
    }

    private LoginHandler Login() => new(_users, _devices, _users, _hasher, _otp, _challenges, _sessions, _options, _clock);

    private VerifyDeviceHandler VerifyDevice() => new(_challenges, _users, _devices, _users, _otp, _sessions, _options, _clock);

    private RefreshSessionHandler Refresh() => new(_refreshTokens, _users, _devices, _users, _sessions, _clock);

    private Task<Result<LoginOutcome>> LoginAsync(string mpin = Mpin, string installation = Phone1) =>
        Login().HandleAsync(new LoginCommand(Phone, mpin, installation, "Test phone"), Cancel);

    /// <summary>Logs in on a new device and completes the OTP step.</summary>
    private async Task<AuthTokens> SignInOnNewDeviceAsync(string installation)
    {
        _otpStore.ClearCooldowns();
        var outcome = await LoginAsync(installation: installation);
        var challenge = outcome.Value.Challenge.ShouldNotBeNull();
        var tokens = await VerifyDevice().HandleAsync(new VerifyDeviceCommand(challenge.ChallengeId, _sms.LastCode()), Cancel);
        return tokens.Value;
    }

    // ---------------- Login ----------------

    [Fact]
    public async Task An_unknown_number_gets_the_same_error_as_a_wrong_mpin_and_still_costs_a_hash()
    {
        var before = _hasher.HashCalls;

        var result = await Login().HandleAsync(new LoginCommand("0311 7654321", Mpin, Phone1, null), Cancel);

        result.Error.Code.ShouldBe(AuthErrors.InvalidCredentialsCode);
        result.Error.Message.ShouldBe("Incorrect mobile number or MPIN.");
        _hasher.HashCalls.ShouldBe(before + 1);
    }

    [Fact]
    public async Task A_wrong_mpin_is_counted_and_reports_the_attempts_left()
    {
        var result = await LoginAsync(mpin: "11223");

        result.Error.Code.ShouldBe(AuthErrors.InvalidCredentialsCode);
        result.Error.Message.ShouldContain("4 attempt(s) left");
        _user.FailedMpinAttempts.ShouldBe(1);
        _users.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task Five_wrong_mpins_lock_the_account_even_for_the_right_mpin()
    {
        for (var i = 0; i < 4; i++)
        {
            await LoginAsync(mpin: "11223");
        }

        var fifth = await LoginAsync(mpin: "11223");
        var thenCorrect = await LoginAsync();

        fifth.Error.Code.ShouldBe(UserErrors.LockedCode);
        fifth.Error.Message.ShouldContain("30 minute(s)");
        thenCorrect.Error.Code.ShouldBe(UserErrors.LockedCode);
        _sms.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_account_unlocks_after_the_lockout_period()
    {
        for (var i = 0; i < 5; i++)
        {
            await LoginAsync(mpin: "11223");
        }

        _clock.Advance(TimeSpan.FromMinutes(31));
        var result = await LoginAsync();

        result.IsSuccess.ShouldBeTrue();
        _user.Status.ShouldBe(UserStatus.Active);
    }

    [Fact]
    public async Task A_lost_concurrency_race_on_a_wrong_mpin_is_rejected_not_ignored()
    {
        _users.ThrowConcurrencyConflictOnSave = true;

        var result = await LoginAsync(mpin: "11223");

        result.Error.ShouldBe(AuthErrors.ConcurrentRequest);
    }

    [Fact]
    public async Task A_new_device_gets_an_otp_challenge_instead_of_tokens()
    {
        var result = await LoginAsync();

        result.Value.Tokens.ShouldBeNull();
        var challenge = result.Value.Challenge.ShouldNotBeNull();
        challenge.ChallengeId.Length.ShouldBe(64);
        challenge.PhoneNumber.ShouldBe("+92300*****67");
        _sms.Messages.ShouldHaveSingleItem().To.ShouldBe("+923001234567");
        _otpStore.Peek(OtpPurpose.DeviceVerification, _user.PhoneNumber).ShouldNotBeNull();
        _accessTokens.Issued.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_invalid_installation_id_is_rejected_before_anything_else()
    {
        var result = await LoginAsync(installation: "short");

        result.Error.ShouldBe(AuthErrors.InvalidDevice);
        _user.FailedMpinAttempts.ShouldBe(0);
    }

    // ---------------- Device verification ----------------

    [Fact]
    public async Task Verifying_the_first_device_trusts_it_without_cooling_off_and_signs_in()
    {
        var tokens = await SignInOnNewDeviceAsync(Phone1);

        var device = _devices.All.ShouldHaveSingleItem();
        device.InstallationId.ShouldBe(Phone1);
        device.IsTrusted.ShouldBeTrue();
        tokens.DeviceId.ShouldBe(device.Id);
        tokens.UserId.ShouldBe(_user.Id);
        tokens.CoolingOffUntil.ShouldBeNull();
        tokens.TokenType.ShouldBe("Bearer");
        tokens.ExpiresIn.ShouldBe(15 * 60);
        tokens.RefreshTokenExpiresIn.ShouldBe(30 * 24 * 60 * 60);
        _challenges.Count.ShouldBe(0);
        _user.LastLoginAt.ShouldBe(_clock.Now);

        var claims = _accessTokens.Issued.ShouldHaveSingleItem();
        claims.UserId.ShouldBe(_user.Id);
        claims.KycTier.ShouldBe(KycTier.L0);
        claims.Role.ShouldBe(UserRole.Customer);
    }

    [Fact]
    public async Task A_trusted_device_logs_in_directly_without_another_otp()
    {
        await SignInOnNewDeviceAsync(Phone1);
        var smsBefore = _sms.Messages.Count;

        var result = await LoginAsync();

        result.Value.Tokens.ShouldNotBeNull();
        result.Value.Challenge.ShouldBeNull();
        _sms.Messages.Count.ShouldBe(smsBefore);
    }

    [Fact]
    public async Task A_second_device_starts_a_24_hour_cooling_off_carried_in_the_token()
    {
        await SignInOnNewDeviceAsync(Phone1);

        var tokens = await SignInOnNewDeviceAsync(Phone2);

        tokens.CoolingOffUntil.ShouldBe(_clock.Now.AddHours(24));
        _accessTokens.Issued[^1].CoolingOffUntil.ShouldBe(_clock.Now.AddHours(24));
    }

    [Fact]
    public async Task A_wrong_device_otp_does_not_trust_the_device()
    {
        var challenge = (await LoginAsync()).Value.Challenge!;
        var wrong = _sms.LastCode() == "000000" ? "111111" : "000000";

        var result = await VerifyDevice().HandleAsync(new VerifyDeviceCommand(challenge.ChallengeId, wrong), Cancel);

        result.Error.Code.ShouldBe("otp.incorrect");
        _devices.All.ShouldBeEmpty();
        _challenges.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_challenge_is_rejected()
    {
        var result = await VerifyDevice().HandleAsync(new VerifyDeviceCommand("does-not-exist", "123456"), Cancel);

        result.Error.ShouldBe(AuthErrors.ChallengeExpired);
    }

    // ---------------- Refresh tokens ----------------

    [Fact]
    public async Task Refreshing_rotates_the_refresh_token_within_the_same_family()
    {
        var first = await SignInOnNewDeviceAsync(Phone1);
        _clock.Advance(TimeSpan.FromMinutes(14));

        var second = await Refresh().HandleAsync(new RefreshSessionCommand(first.RefreshToken), Cancel);

        second.Value.RefreshToken.ShouldNotBe(first.RefreshToken);
        _refreshTokens.All.Count.ShouldBe(2);
        _refreshTokens.All.Select(token => token.FamilyId).Distinct().ShouldHaveSingleItem();
        _refreshTokens.All[0].IsConsumed.ShouldBeTrue();
        _refreshTokens.All[1].IsActive(_clock.Now).ShouldBeTrue();
        _refreshTokens.All.ShouldAllBe(token => token.TokenHash != first.RefreshToken);
    }

    [Fact]
    public async Task Reusing_an_old_refresh_token_revokes_the_whole_family()
    {
        var first = await SignInOnNewDeviceAsync(Phone1);
        var second = (await Refresh().HandleAsync(new RefreshSessionCommand(first.RefreshToken), Cancel)).Value;

        var replay = await Refresh().HandleAsync(new RefreshSessionCommand(first.RefreshToken), Cancel);
        var legitimate = await Refresh().HandleAsync(new RefreshSessionCommand(second.RefreshToken), Cancel);

        replay.Error.ShouldBe(AuthErrors.RefreshTokenReused);
        legitimate.Error.ShouldBe(AuthErrors.InvalidRefreshToken);
        _refreshTokens.All.ShouldAllBe(token => token.IsRevoked);
    }

    [Fact]
    public async Task An_expired_or_unknown_refresh_token_is_rejected()
    {
        var tokens = await SignInOnNewDeviceAsync(Phone1);
        _clock.Advance(TimeSpan.FromDays(31));

        (await Refresh().HandleAsync(new RefreshSessionCommand(tokens.RefreshToken), Cancel)).Error.ShouldBe(AuthErrors.InvalidRefreshToken);
        (await Refresh().HandleAsync(new RefreshSessionCommand("made-up"), Cancel)).Error.ShouldBe(AuthErrors.InvalidRefreshToken);
        (await Refresh().HandleAsync(new RefreshSessionCommand(null), Cancel)).Error.ShouldBe(AuthErrors.InvalidRefreshToken);
    }

    [Fact]
    public async Task Logging_out_ends_the_session()
    {
        var tokens = await SignInOnNewDeviceAsync(Phone1);

        var logout = await new LogoutHandler(_refreshTokens, _users, _clock).HandleAsync(new LogoutCommand(tokens.RefreshToken), Cancel);
        var refresh = await Refresh().HandleAsync(new RefreshSessionCommand(tokens.RefreshToken), Cancel);

        logout.IsSuccess.ShouldBeTrue();
        refresh.Error.ShouldBe(AuthErrors.InvalidRefreshToken);
    }

    // ---------------- Devices ----------------

    [Fact]
    public async Task Revoking_a_device_ends_its_sessions_and_its_next_login_needs_an_otp()
    {
        var tokens = await SignInOnNewDeviceAsync(Phone1);

        var revoke = await new RevokeDeviceHandler(_devices, _refreshTokens, _users, _clock)
            .HandleAsync(_user.Id, tokens.DeviceId, Cancel);
        _otpStore.ClearCooldowns();
        var nextLogin = await LoginAsync();

        revoke.IsSuccess.ShouldBeTrue();
        (await Refresh().HandleAsync(new RefreshSessionCommand(tokens.RefreshToken), Cancel)).IsFailure.ShouldBeTrue();
        nextLogin.Value.Challenge.ShouldNotBeNull();
    }

    [Fact]
    public async Task Another_users_device_cannot_be_revoked()
    {
        var tokens = await SignInOnNewDeviceAsync(Phone1);

        var result = await new RevokeDeviceHandler(_devices, _refreshTokens, _users, _clock)
            .HandleAsync(UserId.New(), tokens.DeviceId, Cancel);

        result.Error.ShouldBe(AccountErrors.DeviceNotFound);
        _devices.All.Single().IsTrusted.ShouldBeTrue();
    }

    [Fact]
    public async Task The_profile_shows_the_masked_number_and_the_current_device()
    {
        var tokens = await SignInOnNewDeviceAsync(Phone1);

        var profile = await new GetProfileHandler(_users, _devices, _clock).HandleAsync(_user.Id, tokens.DeviceId, Cancel);
        var devices = await new ListDevicesHandler(_devices, _clock).HandleAsync(_user.Id, tokens.DeviceId, Cancel);

        profile.Value.PhoneNumber.ShouldBe("+92300*****67");
        profile.Value.Role.ShouldBe("customer");
        profile.Value.CurrentDevice.ShouldNotBeNull().IsCurrent.ShouldBeTrue();
        devices.ShouldHaveSingleItem().Status.ShouldBe(DeviceStatus.Trusted);
    }

    // ---------------- Input and secrets ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("too-short")]
    [InlineData("has spaces in it 1234")]
    [InlineData("semicolons;are;not;allowed")]
    public void Bad_installation_ids_are_rejected(string? installationId) =>
        DeviceInput.Create(installationId, "Phone").Error.ShouldBe(AuthErrors.InvalidDevice);

    [Fact]
    public void A_missing_device_name_gets_a_default_and_a_long_one_is_shortened()
    {
        DeviceInput.Create("0193a2b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b", null).Value.Name.ShouldBe(DeviceInput.DefaultName);
        DeviceInput.Create("0193a2b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b", new string('x', 100)).Value.Name.Length.ShouldBe(Device.NameMaxLength);
    }

    [Fact]
    public void Refresh_tokens_are_long_random_and_stored_only_as_a_hash()
    {
        var a = RefreshTokenSecret.Generate();
        var b = RefreshTokenSecret.Generate();

        a.Length.ShouldBe(43);
        a.ShouldNotBe(b);
        RefreshTokenSecret.Hash(a).Length.ShouldBe(64);
        RefreshTokenSecret.Hash(a).ShouldBe(RefreshTokenSecret.Hash(a));
        RefreshTokenSecret.Hash(a).ShouldNotContain(a);
    }
}
