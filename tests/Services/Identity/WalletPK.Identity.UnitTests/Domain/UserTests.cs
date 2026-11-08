using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.UnitTests.Domain;

public class UserTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_user_is_active_at_the_lowest_kyc_tier()
    {
        var phone = PhoneNumber.Create("03001234567").Value;

        var user = User.Register(phone, "hash", _now);

        user.Status.ShouldBe(UserStatus.Active);
        user.KycTier.ShouldBe(KycTier.L0);
        user.PhoneNumber.ShouldBe(phone);
        user.CreatedAt.ShouldBe(_now);
        user.Id.Value.Version.ShouldBe(7);
    }

    [Fact]
    public void Registering_raises_a_user_registered_event()
    {
        var user = User.Register(PhoneNumber.Create("03001234567").Value, "hash", _now);

        var registered = user.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<UserRegistered>();
        registered.UserId.ShouldBe(user.Id);
        registered.PhoneNumber.ShouldBe("+923001234567");
        registered.OccurredAt.ShouldBe(_now);
    }

    [Fact]
    public void A_user_cannot_be_created_without_an_mpin_hash()
    {
        Should.Throw<ArgumentException>(() => User.Register(PhoneNumber.Create("03001234567").Value, " ", _now));
    }
}
