using WalletPK.Payments.Application.Wallets;

namespace WalletPK.Payments.UnitTests;

public class SplitTests
{
    private static SplitParticipant[] People(params long?[] amounts) =>
        [.. amounts.Select((amount, index) => new SplitParticipant($"+92300000000{index}", amount))];

    [Fact]
    public void Equal_shares_give_the_odd_paisa_to_the_first_participants()
    {
        WalletPaymentsService.Shares(100_000, People(null, null), includeMe: true).ShouldBe([33_334, 33_333]);
        WalletPaymentsService.Shares(100_001, People(null, null), includeMe: false).ShouldBe([50_001, 50_000]);
    }

    [Fact]
    public void Given_shares_must_cover_the_bill_and_never_exceed_it()
    {
        WalletPaymentsService.Shares(100_000, People(60_000, 40_000), includeMe: false).ShouldBe([60_000, 40_000]);
        WalletPaymentsService.Shares(100_000, People(30_000, 40_000), includeMe: true).ShouldBe([30_000, 40_000]);
        WalletPaymentsService.Shares(100_000, People(30_000, 40_000), includeMe: false).ShouldBeNull();
        WalletPaymentsService.Shares(100_000, People(70_000, 40_000), includeMe: true).ShouldBeNull();
    }

    [Fact]
    public void Shares_are_all_given_or_none_and_each_is_at_least_a_rupee()
    {
        WalletPaymentsService.Shares(100_000, People(60_000, null), includeMe: true).ShouldBeNull();
        WalletPaymentsService.Shares(150, People(null, null), includeMe: true).ShouldBeNull();
    }

    [Fact]
    public void Each_payment_attempt_of_a_request_has_its_own_transfer_id()
    {
        var request = Guid.NewGuid();
        MoneyRequestSaga.PaymentIdFor(request, 1).ShouldBe(MoneyRequestSaga.PaymentIdFor(request, 1));
        MoneyRequestSaga.PaymentIdFor(request, 2).ShouldNotBe(MoneyRequestSaga.PaymentIdFor(request, 1));
    }
}
