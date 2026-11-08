using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.UnitTests.Domain;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("03001234567")]
    [InlineData("0300 1234567")]
    [InlineData("0300-1234567")]
    [InlineData("+923001234567")]
    [InlineData("+92 300 123 4567")]
    [InlineData("923001234567")]
    [InlineData("00923001234567")]
    [InlineData("3001234567")]
    public void Every_common_format_becomes_the_same_e164_number(string input)
    {
        var phone = PhoneNumber.Create(input);

        phone.IsSuccess.ShouldBeTrue();
        phone.Value.Value.ShouldBe("+923001234567");
    }

    [Theory]
    [InlineData(null, "phone.required")]
    [InlineData("   ", "phone.required")]
    [InlineData("12345", "phone.invalid")]
    [InlineData("04235761234", "phone.invalid")]
    [InlineData("+14155552671", "phone.invalid")]
    [InlineData("0300123456", "phone.invalid")]
    [InlineData("not a number", "phone.invalid")]
    public void Non_pakistani_mobile_numbers_are_rejected(string? input, string expectedCode)
    {
        var phone = PhoneNumber.Create(input);

        phone.IsFailure.ShouldBeTrue();
        phone.Error.Code.ShouldBe(expectedCode);
    }

    [Fact]
    public void Masked_form_hides_the_middle_digits()
    {
        PhoneNumber.Create("03001234567").Value.Masked.ShouldBe("+92300*****67");
    }

    [Fact]
    public void Numbers_compare_by_value()
    {
        PhoneNumber.Create("0300 1234567").Value.ShouldBe(PhoneNumber.Create("+923001234567").Value);
    }
}
