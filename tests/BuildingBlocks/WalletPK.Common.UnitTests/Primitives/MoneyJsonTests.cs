using System.Text.Json;
using WalletPK.Common.Primitives;

namespace WalletPK.Common.UnitTests.Primitives;

public class MoneyJsonTests
{
    [Fact]
    public void Serialises_as_minor_units_and_currency()
    {
        var json = JsonSerializer.Serialize(Money.FromRupees(1250.50m));

        json.ShouldBe("""{"minorUnits":125050,"currency":"PKR"}""");
    }

    [Fact]
    public void Round_trips_exactly()
    {
        var original = Money.FromMajorUnits(99.99m, Currency.Usd);

        var copy = JsonSerializer.Deserialize<Money>(JsonSerializer.Serialize(original));

        copy.ShouldBe(original);
    }

    [Fact]
    public void Currency_defaults_to_pkr_and_names_are_case_insensitive()
    {
        JsonSerializer.Deserialize<Money>("""{"MinorUnits":500}""").ShouldBe(Money.FromPaisa(500));
    }

    [Theory]
    [InlineData("12.5")]
    [InlineData("""{"currency":"PKR"}""")]
    [InlineData("""{"minorUnits":1,"currency":"XYZ"}""")]
    [InlineData("""{"minorUnits":1.5}""")]
    public void Rejects_invalid_money_json(string json)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Money>(json));
    }
}
