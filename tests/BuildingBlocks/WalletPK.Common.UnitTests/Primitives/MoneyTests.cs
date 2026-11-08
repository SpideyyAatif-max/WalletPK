using WalletPK.Common.Primitives;
using WalletPK.Common.Results;

namespace WalletPK.Common.UnitTests.Primitives;

public class MoneyTests
{
    [Fact]
    public void FromRupees_stores_the_amount_in_paisa()
    {
        var money = Money.FromRupees(1250.50m);

        money.MinorUnits.ShouldBe(125050);
        money.Currency.ShouldBe(Currency.Pkr);
        money.ToMajorUnits().ShouldBe(1250.50m);
    }

    [Theory]
    [InlineData(10.005)]
    [InlineData(0.001)]
    public void FromRupees_rejects_more_than_two_decimal_places(double rupees)
    {
        Should.Throw<ArgumentException>(() => Money.FromRupees((decimal)rupees));
    }

    [Fact]
    public void TryFromMajorUnits_returns_a_validation_error_instead_of_throwing()
    {
        var result = Money.TryFromMajorUnits(99.999m);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Code.ShouldBe("money.invalid_precision");
    }

    [Fact]
    public void Addition_and_subtraction_are_exact()
    {
        var balance = Money.FromRupees(1000m);

        var afterDeposit = balance + Money.FromRupees(0.10m) + Money.FromRupees(0.20m);
        var afterWithdrawal = afterDeposit - Money.FromRupees(0.30m);

        afterDeposit.ShouldBe(Money.FromRupees(1000.30m));
        afterWithdrawal.ShouldBe(balance);
    }

    [Fact]
    public void Mixing_currencies_is_not_allowed()
    {
        var rupees = Money.FromRupees(100m);
        var dollars = Money.FromMajorUnits(100m, Currency.Usd);

        Should.Throw<InvalidOperationException>(() => rupees + dollars);
        Should.Throw<InvalidOperationException>(() => rupees < dollars);
    }

    [Fact]
    public void Overflow_throws_instead_of_wrapping_around()
    {
        var max = Money.FromPaisa(long.MaxValue);

        Should.Throw<OverflowException>(() => max + Money.FromPaisa(1));
        Should.Throw<OverflowException>(() => max * 2);
    }

    [Fact]
    public void Percent_rounds_half_away_from_zero_by_default()
    {
        // 2.5% of Rs. 999.99 = 2499.975 paisa -> 2500 paisa
        var fee = Money.FromRupees(999.99m).Percent(2.5m);

        fee.MinorUnits.ShouldBe(2500);
    }

    [Fact]
    public void Percent_can_use_bankers_rounding()
    {
        // 0.5% of Rs. 5.00 = 2.5 paisa -> 2 paisa with banker's rounding (to even)
        var fee = Money.FromRupees(5m).Percent(0.5m, MidpointRounding.ToEven);

        fee.MinorUnits.ShouldBe(2);
    }

    [Fact]
    public void Split_never_loses_or_creates_a_paisa()
    {
        var shares = Money.FromRupees(100m).Split(3);

        shares.Select(s => s.MinorUnits).ToArray().ShouldBe(new long[] { 3334, 3333, 3333 });
        shares.Aggregate(Money.Zero(), (sum, s) => sum + s).ShouldBe(Money.FromRupees(100m));
    }

    [Theory]
    [InlineData(1L, 7)]
    [InlineData(99L, 4)]
    [InlineData(100_00L, 3)]
    [InlineData(123_456_789L, 11)]
    [InlineData(-1_000L, 3)]
    public void Split_shares_always_add_up_to_the_original(long paisa, int parts)
    {
        var original = Money.FromPaisa(paisa);

        var shares = original.Split(parts);

        shares.Count.ShouldBe(parts);
        shares.Aggregate(Money.Zero(), (sum, s) => sum + s).ShouldBe(original);
        (shares.Max(s => s.MinorUnits) - shares.Min(s => s.MinorUnits)).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Allocate_by_ratio_splits_a_merchant_payment_into_net_and_fee()
    {
        // Rs. 1,000 split 98:2 between the merchant and the platform
        var parts = Money.FromRupees(1000m).Allocate(98, 2);

        parts[0].ShouldBe(Money.FromRupees(980m));
        parts[1].ShouldBe(Money.FromRupees(20m));
    }

    [Fact]
    public void Allocate_rejects_invalid_ratios()
    {
        var money = Money.FromRupees(10m);

        Should.Throw<ArgumentException>(() => money.Allocate());
        Should.Throw<ArgumentException>(() => money.Allocate(0, 0));
        Should.Throw<ArgumentException>(() => money.Allocate(1, -1));
    }

    [Fact]
    public void Comparison_operators_work_for_the_same_currency()
    {
        var small = Money.FromRupees(10m);
        var large = Money.FromRupees(20m);

        (small < large).ShouldBeTrue();
        (large >= small).ShouldBeTrue();
        Money.Max(small, large).ShouldBe(large);
        Money.Min(small, large).ShouldBe(small);
    }

    [Fact]
    public void Default_value_is_zero_rupees()
    {
        var money = default(Money);

        money.ShouldBe(Money.Zero());
        money.Currency.ShouldBe(Currency.Pkr);
        money.IsZero.ShouldBeTrue();
    }

    [Fact]
    public void Formatting_uses_the_currency_code_or_symbol()
    {
        var money = Money.FromRupees(1250.5m);

        money.ToString().ShouldBe("PKR 1,250.50");
        money.ToDisplayString().ShouldBe("Rs. 1,250.50");
        (-money).ToString().ShouldBe("PKR -1,250.50");
    }

    [Fact]
    public void Sign_helpers_report_correctly()
    {
        Money.FromPaisa(5).IsPositive.ShouldBeTrue();
        Money.FromPaisa(-5).IsNegative.ShouldBeTrue();
        Money.FromPaisa(-5).Abs().ShouldBe(Money.FromPaisa(5));
    }
}
