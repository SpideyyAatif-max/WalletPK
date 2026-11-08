using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.UnitTests.Domain;

public class UserLockoutTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _lockout = TimeSpan.FromMinutes(30);

    private static User NewUser() => User.Register(PhoneNumber.Create("03001234567").Value, "hash", _now);

    [Fact]
    public void A_new_user_is_an_active_customer_with_no_failed_attempts()
    {
        var user = NewUser();

        user.Role.ShouldBe(UserRole.Customer);
        user.FailedMpinAttempts.ShouldBe(0);
        user.LockedUntil.ShouldBeNull();
        user.EnsureCanLogIn(_now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Wrong_mpins_below_the_limit_are_only_counted()
    {
        var user = NewUser();

        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedMpin(_now, maxAttempts: 5, _lockout).ShouldBeFalse();
        }

        user.FailedMpinAttempts.ShouldBe(4);
        user.Status.ShouldBe(UserStatus.Active);
    }

    [Fact]
    public void The_fifth_wrong_mpin_locks_the_account_and_raises_an_event()
    {
        var user = NewUser();
        user.ClearDomainEvents();

        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedMpin(_now, 5, _lockout);
        }

        user.RecordFailedMpin(_now, 5, _lockout).ShouldBeTrue();

        user.Status.ShouldBe(UserStatus.Locked);
        user.LockedUntil.ShouldBe(_now + _lockout);
        user.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<UserLockedOut>();

        var check = user.EnsureCanLogIn(_now.AddMinutes(10));
        check.Error.Code.ShouldBe(UserErrors.LockedCode);
        check.Error.Message.ShouldContain("20 minute(s)");
    }

    [Fact]
    public void A_lockout_lifts_itself_once_it_has_run_out()
    {
        var user = NewUser();
        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedMpin(_now, 5, _lockout);
        }

        user.EnsureCanLogIn(_now + _lockout).IsSuccess.ShouldBeTrue();

        user.Status.ShouldBe(UserStatus.Active);
        user.LockedUntil.ShouldBeNull();
        user.FailedMpinAttempts.ShouldBe(0);
    }

    [Fact]
    public void A_successful_login_resets_the_counter()
    {
        var user = NewUser();
        user.RecordFailedMpin(_now, 5, _lockout);
        user.RecordFailedMpin(_now, 5, _lockout);

        user.RecordSuccessfulLogin(_now.AddMinutes(1));

        user.FailedMpinAttempts.ShouldBe(0);
        user.LastLoginAt.ShouldBe(_now.AddMinutes(1));
    }

    [Theory]
    [InlineData(UserRole.Customer, "customer")]
    [InlineData(UserRole.Ops, "ops")]
    [InlineData(UserRole.Admin, "admin")]
    public void Roles_map_to_the_claim_values_the_gateway_expects(UserRole role, string expected) =>
        role.ToClaimValue().ShouldBe(expected);
}
