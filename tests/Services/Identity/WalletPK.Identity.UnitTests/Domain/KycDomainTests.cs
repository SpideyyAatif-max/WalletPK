using WalletPK.Identity.Domain.Kyc;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.UnitTests.Domain;

public class KycDomainTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("35202-1234567-1")]
    [InlineData("3520212345671")]
    [InlineData(" 35202-1234567-1 ")]
    public void A_cnic_is_accepted_with_or_without_dashes(string input)
    {
        var cnic = Cnic.Create(input).Value;

        cnic.Value.ShouldBe("3520212345671");
        cnic.Formatted.ShouldBe("35202-1234567-1");
        cnic.Masked.ShouldBe("35202-*******-1");
        cnic.ToString().ShouldBe("35202-*******-1");
    }

    [Theory]
    [InlineData(null, "cnic.required")]
    [InlineData("", "cnic.required")]
    [InlineData("35202-123456-1", "cnic.invalid")]
    [InlineData("352021234567", "cnic.invalid")]
    [InlineData("3520-21234567-1", "cnic.invalid")]
    [InlineData("85202-1234567-1", "cnic.invalid")]
    [InlineData("05202-1234567-1", "cnic.invalid")]
    [InlineData("11111-1111111-1", "cnic.invalid")]
    [InlineData("35202 1234567 1", "cnic.invalid")]
    public void Malformed_cnics_are_rejected(string? input, string expectedCode) =>
        Cnic.Create(input).Error.Code.ShouldBe(expectedCode);

    [Fact]
    public void Upgrading_the_tier_raises_kyc_upgraded_and_tiers_never_go_down()
    {
        var user = User.Register(PhoneNumber.Create("03001234567").Value, "hash", _now);
        user.ClearDomainEvents();

        user.UpgradeKycTier(KycTier.L1, _now).IsSuccess.ShouldBeTrue();
        var again = user.UpgradeKycTier(KycTier.L1, _now);
        var down = user.UpgradeKycTier(KycTier.L0, _now);

        user.KycTier.ShouldBe(KycTier.L1);
        var raised = user.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<KycUpgraded>();
        raised.PreviousTier.ShouldBe(KycTier.L0);
        raised.NewTier.ShouldBe(KycTier.L1);
        again.Error.Code.ShouldBe("kyc.already_verified");
        down.Error.Code.ShouldBe("kyc.already_verified");
    }

    [Fact]
    public void A_locked_account_cannot_transact_until_the_lockout_ends()
    {
        var user = User.Register(PhoneNumber.Create("03001234567").Value, "hash", _now);
        user.CanTransact(_now).ShouldBeTrue();

        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedMpin(_now, 5, TimeSpan.FromMinutes(30));
        }

        user.CanTransact(_now.AddMinutes(29)).ShouldBeFalse();
        user.CanTransact(_now.AddMinutes(30)).ShouldBeTrue();
    }
}
