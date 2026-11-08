using WalletPK.Common.Primitives;

namespace WalletPK.Common.UnitTests.Primitives;

public class CurrencyTests
{
    [Theory]
    [InlineData("PKR")]
    [InlineData("pkr")]
    public void FromCode_is_case_insensitive(string code)
    {
        Currency.FromCode(code).ShouldBe(Currency.Pkr);
    }

    [Fact]
    public void FromCode_rejects_unsupported_currencies()
    {
        Should.Throw<ArgumentException>(() => Currency.FromCode("XYZ"));
        Currency.TryFromCode("XYZ", out _).ShouldBeFalse();
    }

    [Fact]
    public void Pkr_has_100_paisa_per_rupee()
    {
        Currency.Pkr.MinorUnitsPerMajorUnit.ShouldBe(100);
    }
}
