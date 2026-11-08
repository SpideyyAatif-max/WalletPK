using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Otp;
using WalletPK.Identity.Domain.Users;
using WalletPK.Identity.UnitTests.Fakes;

namespace WalletPK.Identity.UnitTests.Application;

public class OtpServiceTests
{
    private static readonly PhoneNumber _phone = PhoneNumber.Create("03001234567").Value;
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private readonly InMemoryOtpStore _store = new();
    private readonly CapturingSmsSender _sms = new();
    private readonly OtpService _service;

    public OtpServiceTests()
    {
        _service = new OtpService(_store, _sms, new OtpOptions
        {
            Pepper = "unit-test-pepper-that-is-at-least-32-chars",
            MaxAttempts = 3,
            TimeToLiveSeconds = 120,
        });
    }

    [Fact]
    public async Task Issuing_sends_a_six_digit_code_and_stores_only_its_hash()
    {
        var issued = await _service.IssueAsync(OtpPurpose.Registration, _phone, Cancel);

        issued.Value.ExpiresInSeconds.ShouldBe(120);
        var code = _sms.LastCode();
        code.Length.ShouldBe(6);

        var stored = _store.Peek(OtpPurpose.Registration, _phone).ShouldNotBeNull();
        stored.CodeHash.ShouldNotContain(code);
        stored.CodeHash.Length.ShouldBe(64);
        _store.TimeToLive.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task The_correct_code_works_exactly_once()
    {
        await _service.IssueAsync(OtpPurpose.Registration, _phone, Cancel);
        var code = _sms.LastCode();

        (await _service.VerifyAsync(OtpPurpose.Registration, _phone, code, Cancel)).IsSuccess.ShouldBeTrue();
        (await _service.VerifyAsync(OtpPurpose.Registration, _phone, code, Cancel)).Error.Code.ShouldBe("otp.expired");
    }

    [Fact]
    public async Task A_code_for_one_purpose_cannot_be_used_for_another()
    {
        await _service.IssueAsync(OtpPurpose.Registration, _phone, Cancel);

        var result = await _service.VerifyAsync(OtpPurpose.DeviceVerification, _phone, _sms.LastCode(), Cancel);

        result.Error.Code.ShouldBe("otp.expired");
    }

    [Fact]
    public async Task Wrong_codes_count_down_then_destroy_the_code()
    {
        await _service.IssueAsync(OtpPurpose.Registration, _phone, Cancel);
        var code = _sms.LastCode();
        var wrong = code == "000000" ? "111111" : "000000";

        var first = await _service.VerifyAsync(OtpPurpose.Registration, _phone, wrong, Cancel);
        var second = await _service.VerifyAsync(OtpPurpose.Registration, _phone, wrong, Cancel);
        var third = await _service.VerifyAsync(OtpPurpose.Registration, _phone, wrong, Cancel);
        var correctButTooLate = await _service.VerifyAsync(OtpPurpose.Registration, _phone, code, Cancel);

        first.Error.Message.ShouldContain("2 attempt(s) left");
        second.Error.Message.ShouldContain("1 attempt(s) left");
        third.Error.Code.ShouldBe("otp.too_many_attempts");
        third.Error.Type.ShouldBe(ErrorType.TooManyRequests);
        correctButTooLate.Error.Code.ShouldBe("otp.expired");
    }

    [Fact]
    public async Task A_new_code_cannot_be_requested_during_the_cooldown()
    {
        await _service.IssueAsync(OtpPurpose.Registration, _phone, Cancel);

        var again = await _service.IssueAsync(OtpPurpose.Registration, _phone, Cancel);

        again.Error.Code.ShouldBe("otp.resend_too_soon");
        again.Error.Message.ShouldContain("12 seconds");
        _sms.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Requesting_a_new_code_invalidates_the_previous_one()
    {
        await _service.IssueAsync(OtpPurpose.Registration, _phone, Cancel);
        var oldCode = _sms.LastCode();
        _store.ClearCooldowns();
        await _service.IssueAsync(OtpPurpose.Registration, _phone, Cancel);
        var newCode = _sms.LastCode();

        if (oldCode != newCode)
        {
            (await _service.VerifyAsync(OtpPurpose.Registration, _phone, oldCode, Cancel)).IsFailure.ShouldBeTrue();
        }

        (await _service.VerifyAsync(OtpPurpose.Registration, _phone, newCode, Cancel)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Generated_codes_are_zero_padded_digits()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => OtpService.GenerateCode(6)).ToArray();

        codes.ShouldAllBe(code => code.Length == 6 && code.All(c => char.IsAsciiDigit(c)));
        codes.Distinct().Count().ShouldBeGreaterThan(150);
    }
}
