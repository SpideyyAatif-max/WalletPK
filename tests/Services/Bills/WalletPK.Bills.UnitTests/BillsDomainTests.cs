using WalletPK.Bills.Application.Payments;
using WalletPK.Bills.Domain;

namespace WalletPK.Bills.UnitTests;

public class BillsDomainTests
{
    private static Biller Ke => BillerCatalog.ByCode("ke")!;

    [Theory]
    [InlineData("0400012345678", "0400012345678")]
    [InlineData("04000 1234 5678", "0400012345678")]
    [InlineData("0400-0123-45678", "0400012345678")]
    [InlineData("040001234567", null)]
    [InlineData("04000123456789", null)]
    [InlineData("04000123456A8", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Consumer_numbers_are_checked_against_the_billers_format(string? input, string? expected) =>
        BillerCatalog.NormalizeConsumerNumber(Ke, input).ShouldBe(expected);

    [Fact]
    public void The_catalog_finds_billers_by_code_in_any_case()
    {
        BillerCatalog.ByCode("lesco")!.ConsumerNumberLength.ShouldBe(14);
        BillerCatalog.ByCode("WAPDA").ShouldBeNull();
        BillerCatalog.All.Select(biller => biller.Code).ShouldBe(["KE", "LESCO", "SNGPL", "PTCL"]);
    }

    [Theory]
    [InlineData("Unpaid", "2026-11-20", "2026-11-17", null, true)]
    [InlineData("Unpaid", "2026-11-20", "2026-11-16", null, false)]
    [InlineData("Unpaid", "2026-11-20", "2026-11-20", null, true)]
    [InlineData("Unpaid", "2026-11-20", "2026-11-25", null, true)]
    [InlineData("Unpaid", "2026-11-20", "2026-11-18", "2026-11", false)]
    [InlineData("Unpaid", "2026-11-20", "2026-11-18", "2026-10", true)]
    [InlineData("Paid", "2026-11-20", "2026-11-18", null, false)]
    public void Reminders_go_out_once_per_unpaid_bill_from_three_days_before_it_is_due(string status, string due, string today, string? reminded, bool expected) =>
        BillReminderRule.ShouldRemind(status, DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture), "2026-11", DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture), reminded, daysBefore: 3)
            .ShouldBe(expected);

    [Fact]
    public void The_same_key_gives_the_same_payment_id_per_customer()
    {
        var user = Guid.NewGuid();
        BillsService.PaymentIdFor(user, "k").ShouldBe(BillsService.PaymentIdFor(user, "k"));
        BillsService.PaymentIdFor(user, "k").ShouldNotBe(BillsService.PaymentIdFor(Guid.NewGuid(), "k"));
    }

    [Fact]
    public void Retries_back_off_up_to_a_cap()
    {
        BillPaymentSaga.Backoff(10, 120, 1).ShouldBe(TimeSpan.FromSeconds(10));
        BillPaymentSaga.Backoff(10, 120, 3).ShouldBe(TimeSpan.FromSeconds(40));
        BillPaymentSaga.Backoff(10, 120, 9).ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void The_reserved_money_must_outlive_every_automatic_step()
    {
        new BillPaymentOptions().IsValid().ShouldBeTrue();
        new BillPaymentOptions { HoldLifetimeHours = 48 }.IsValid().ShouldBeFalse();
    }

    [Fact]
    public void Validation_names_the_billers_format()
    {
        var result = BillsService.Validate("SNGPL", "123");
        result.IsFailure.ShouldBeTrue();
        result.Error.Message.ShouldContain("11 digits");
        BillsService.Validate("NOPE", "123").Error.Code.ShouldBe("bills.unknown_biller");
    }
}
