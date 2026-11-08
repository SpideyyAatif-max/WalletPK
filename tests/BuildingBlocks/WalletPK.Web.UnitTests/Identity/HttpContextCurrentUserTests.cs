using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using WalletPK.Common.Identifiers;
using WalletPK.Web.Identity;

namespace WalletPK.Web.UnitTests.Identity;

public class HttpContextCurrentUserTests
{
    private static HttpContextCurrentUser CreateUser(params Claim[] claims)
    {
        var identity = claims.Length == 0
            ? new ClaimsIdentity()
            : new ClaimsIdentity(claims, authenticationType: "Bearer");

        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        return new HttpContextCurrentUser(new HttpContextAccessor { HttpContext = context });
    }

    [Fact]
    public void Reads_the_user_device_tier_and_role_from_the_token()
    {
        var userId = UserId.New();

        var user = CreateUser(
            new Claim(WalletPkClaimTypes.Subject, userId.ToString()),
            new Claim(WalletPkClaimTypes.DeviceId, "pixel-8-abc"),
            new Claim(WalletPkClaimTypes.KycTier, "L1"),
            new Claim(WalletPkClaimTypes.Role, "customer"));

        user.IsAuthenticated.ShouldBeTrue();
        user.Id.ShouldBe(userId);
        user.GetRequiredId().ShouldBe(userId);
        user.DeviceId.ShouldBe("pixel-8-abc");
        user.KycTier.ShouldBe("L1");
        user.IsInRole("customer").ShouldBeTrue();
        user.IsInRole("admin").ShouldBeFalse();
    }

    [Fact]
    public void Falls_back_to_the_mapped_name_identifier_claim()
    {
        var userId = UserId.New();

        var user = CreateUser(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));

        user.Id.ShouldBe(userId);
    }

    [Fact]
    public void Anonymous_requests_have_no_user()
    {
        var user = CreateUser();

        user.IsAuthenticated.ShouldBeFalse();
        user.Id.ShouldBeNull();
        user.DeviceId.ShouldBeNull();
        user.IsInRole("customer").ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => user.GetRequiredId());
    }

    [Fact]
    public void A_malformed_subject_is_treated_as_no_user_id()
    {
        var user = CreateUser(new Claim(WalletPkClaimTypes.Subject, "not-a-guid"));

        user.IsAuthenticated.ShouldBeTrue();
        user.Id.ShouldBeNull();
    }
}
