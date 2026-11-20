using System.Text;
using WalletPK.Agents.Application;
using WalletPK.Agents.Domain;

namespace WalletPK.Agents.UnitTests;

public class CommissionPlanTests
{
    [Theory]
    [InlineData(10_000, 200)] // Rs. 100: 0.4% is Rs. 0.40, the floor is Rs. 2
    [InlineData(200_000, 800)] // Rs. 2,000: Rs. 8
    [InlineData(5_000_000, 4_000)] // Rs. 50,000: 0.4% is Rs. 200, the ceiling is Rs. 40
    public void A_cash_in_is_free_for_the_customer_and_earns_the_agent_a_commission(long amount, long commission)
    {
        var quote = CommissionPlan.CashIn(amount);

        quote.ShouldBe(new AgentQuote(amount, 0, 0, commission, amount));
    }

    [Theory]
    [InlineData(10_000, 1_000, 160, 600)] // Rs. 100: the Rs. 10 floor
    [InlineData(250_000, 2_500, 400, 1_500)] // Rs. 2,500: 1%
    [InlineData(5_000_000, 30_000, 4_800, 18_000)] // Rs. 50,000: the Rs. 300 ceiling
    public void A_cash_out_costs_a_fee_and_its_tax_on_top_and_the_agent_earns_60_percent_of_the_fee(long amount, long fee, long tax, long commission)
    {
        var quote = CommissionPlan.CashOut(amount);

        quote.ShouldBe(new AgentQuote(amount, fee, tax, commission, amount + fee + tax));
    }

    [Theory]
    [InlineData(3_200, 384)]
    [InlineData(400, 48)]
    [InlineData(5, 1)] // 0.6 rounds half up
    public void Withholding_on_commission_is_12_percent_rounded_half_up(long gross, long tax) =>
        CommissionPlan.Withholding(gross).ShouldBe(tax);

    [Theory]
    [InlineData(10_000, true)]
    [InlineData(5_000_000, true)]
    [InlineData(123_400, true)]
    [InlineData(9_900, false)]
    [InlineData(5_000_100, false)]
    [InlineData(10_050, false)]
    public void Cash_is_whole_rupees_from_100_to_50000(long paisa, bool valid) => AgentRules.IsValidCash(paisa).ShouldBe(valid);
}

public class CashOutCodeTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("unit-test-cash-out-code-key-0123456789");

    [Fact]
    public void Codes_are_six_digits()
    {
        for (var i = 0; i < 500; i++)
        {
            CashOutCodes.NewCode().ShouldMatch("^[0-9]{6}$");
        }
    }

    [Fact]
    public void A_code_matches_only_its_own_request_and_key()
    {
        var request = Guid.NewGuid();
        var hash = CashOutCodes.Hash(request, "123456", Key);

        CashOutCodes.Matches(request, "123456", hash, Key).ShouldBeTrue();
        CashOutCodes.Matches(request, "123457", hash, Key).ShouldBeFalse();
        CashOutCodes.Matches(Guid.NewGuid(), "123456", hash, Key).ShouldBeFalse();
        CashOutCodes.Matches(request, "123456", hash, Encoding.UTF8.GetBytes("another-key-0123456789-0123456789")).ShouldBeFalse();
        hash.ShouldNotContain("123456");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("١٢٣٤٥٦")]
    public void Anything_but_six_ascii_digits_never_matches(string? code) =>
        CashOutCodes.Matches(Guid.NewGuid(), code, new string('0', 64), Key).ShouldBeFalse();
}

public class AgentHelperTests
{
    [Fact]
    public void Agent_codes_are_WPKA_and_six_digits_and_stable()
    {
        var id = Guid.NewGuid();
        AgentService.CodeFor(id).ShouldBe(AgentService.CodeFor(id));
        for (var i = 0; i < 200; i++)
        {
            AgentService.CodeFor(Guid.CreateVersion7()).ShouldMatch("^WPKA[0-9]{6}$");
        }
    }

    [Fact]
    public void Saga_ids_are_the_same_for_the_same_inputs_and_differ_otherwise()
    {
        var user = Guid.NewGuid();
        AgentService.SagaId("cash-in", user, "key").ShouldBe(AgentService.SagaId("cash-in", user, "key"));
        AgentService.SagaId("cash-in", user, "key").ShouldNotBe(AgentService.SagaId("cash-out", user, "key"));
        AgentService.SagaId("cash-in", user, "key").Version.ShouldBe(8);
    }

    [Theory]
    [InlineData("CashIn", 10_000, true)]
    [InlineData("CashOut", 10_000, true)]
    [InlineData("Teleport", 10_000, false)]
    [InlineData("CashOut", 50, false)]
    public void Quotes_need_a_known_kind_and_a_valid_amount(string kind, long amount, bool ok) =>
        AgentService.Quote(kind, amount).IsSuccess.ShouldBe(ok);
}
