using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.UnitTests.Domain;

public class MpinPolicyTests
{
    [Theory]
    [InlineData("40291")]
    [InlineData("13579")]
    [InlineData("90123")]
    public void Reasonable_mpins_are_accepted(string mpin)
    {
        MpinPolicy.Validate(mpin).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("123456")]
    [InlineData("12a45")]
    [InlineData("١٢٣٤٥")]
    public void Anything_but_five_digits_is_rejected(string? mpin)
    {
        MpinPolicy.Validate(mpin).Error.Code.ShouldBe("mpin.invalid_format");
    }

    [Theory]
    [InlineData("11111")]
    [InlineData("00000")]
    [InlineData("12345")]
    [InlineData("01234")]
    [InlineData("54321")]
    [InlineData("98765")]
    public void Easily_guessed_mpins_are_rejected(string mpin)
    {
        MpinPolicy.Validate(mpin).Error.Code.ShouldBe("mpin.too_simple");
    }
}
