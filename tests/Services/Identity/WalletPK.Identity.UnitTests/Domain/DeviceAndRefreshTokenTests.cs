using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Sessions;

namespace WalletPK.Identity.UnitTests.Domain;

public class DeviceAndRefreshTokenTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly UserId _user = UserId.New();

    [Fact]
    public void A_first_device_has_no_cooling_off()
    {
        var device = Device.Trust(_user, "install-0000000001", "Pixel 8", _now, coolingOff: null);

        device.IsTrusted.ShouldBeTrue();
        device.ActiveCoolingOffUntil(_now).ShouldBeNull();
        device.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<DeviceTrusted>();
    }

    [Fact]
    public void Cooling_off_is_active_until_it_ends()
    {
        var device = Device.Trust(_user, "install-0000000002", "Galaxy A54", _now, TimeSpan.FromHours(24));

        device.ActiveCoolingOffUntil(_now.AddHours(23)).ShouldBe(_now.AddHours(24));
        device.ActiveCoolingOffUntil(_now.AddHours(24)).ShouldBeNull();
    }

    [Fact]
    public void Revoking_is_idempotent_and_trusting_again_restarts_cooling_off()
    {
        var device = Device.Trust(_user, "install-0000000003", "Old phone", _now, null);
        device.ClearDomainEvents();

        device.Revoke(_now.AddDays(1));
        device.Revoke(_now.AddDays(2));

        device.IsTrusted.ShouldBeFalse();
        device.RevokedAt.ShouldBe(_now.AddDays(1));
        device.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<DeviceRevoked>();

        device.TrustAgain("Old phone", _now.AddDays(3), TimeSpan.FromHours(24));

        device.IsTrusted.ShouldBeTrue();
        device.RevokedAt.ShouldBeNull();
        device.ActiveCoolingOffUntil(_now.AddDays(3)).ShouldBe(_now.AddDays(4));
    }

    [Fact]
    public void A_refresh_token_is_active_until_consumed_revoked_or_expired()
    {
        var token = RefreshToken.Issue(_user, DeviceId.New(), Guid.NewGuid(), "hash-1", _now, TimeSpan.FromDays(30));

        token.IsActive(_now).ShouldBeTrue();
        token.IsActive(_now.AddDays(30)).ShouldBeFalse();

        token.Consume(_now.AddMinutes(1));
        token.IsActive(_now.AddMinutes(2)).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => token.Consume(_now.AddMinutes(2)));
    }

    [Fact]
    public void Revoking_a_refresh_token_keeps_the_first_revocation_time()
    {
        var token = RefreshToken.Issue(_user, DeviceId.New(), Guid.NewGuid(), "hash-2", _now, TimeSpan.FromDays(30));

        token.Revoke(_now.AddMinutes(1));
        token.Revoke(_now.AddMinutes(5));

        token.RevokedAt.ShouldBe(_now.AddMinutes(1));
        token.IsActive(_now.AddMinutes(2)).ShouldBeFalse();
    }
}
