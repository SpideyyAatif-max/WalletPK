using WalletPK.Common.Primitives;

namespace WalletPK.Common.UnitTests.Primitives;

public class PakistaniMobileNumberTests
{
    [Theory]
    [InlineData("03001234567")]
    [InlineData("0300 1234567")]
    [InlineData("0300-1234567")]
    [InlineData("+92 300 1234567")]
    [InlineData("923001234567")]
    [InlineData("00923001234567")]
    [InlineData("3001234567")]
    public void Every_common_format_normalises_to_e164(string input)
    {
        PakistaniMobileNumber.TryNormalize(input, out var e164).ShouldBeTrue();
        e164.ShouldBe("+923001234567");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("04235761234")]      // a Lahore landline
    [InlineData("030012345678")]     // one digit too many
    [InlineData("+44 7700 900123")]  // not Pakistani
    public void Anything_else_is_rejected(string? input) =>
        PakistaniMobileNumber.TryNormalize(input, out _).ShouldBeFalse();

    [Fact]
    public void Masking_keeps_the_network_prefix_and_last_two_digits() =>
        PakistaniMobileNumber.Mask("+923001234567").ShouldBe("+92300*****67");
}
