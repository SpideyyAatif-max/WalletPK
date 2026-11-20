using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Contracts.Ledger;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using WalletPK.Messaging;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>M9: agent floats, cash-in, cash-out, float moves and commission payouts, over ledger.commands.</summary>
[Collection(LedgerCollection.Name)]
public class AgentCommandTests(LedgerApiFactory factory)
{
    private IntegrationEventRegistry Registry => factory.Services.GetRequiredService<IntegrationEventRegistry>();

    private async Task<ReadMessage> SendAsync(IntegrationEvent command, string key, int nth = 1)
    {
        await factory.PublishAsync(command, Registry, correlationId: $"agent-{key}");
        bool Mine(ReadMessage message) => message.Key == key;
        return factory.ReadUntil(KafkaTopics.LedgerReplies, read => read.Count(Mine) >= nth, TimeSpan.FromSeconds(30)).Where(Mine).ElementAt(nth - 1);
    }

    private async Task<(Customer Owner, Guid AgentId, AccountId Float)> AgentAsync(decimal walletRupees = 0)
    {
        var owner = await factory.CustomerAsync(rupees: walletRupees);
        var agentId = Guid.CreateVersion7();
        var code = $"WPKA{Random.Shared.Next(100_000, 999_999)}";
        var opened = await SendAsync(new OpenAgentWallet(agentId, owner.Id.Value, code, "Bismillah Easy Cash"), agentId.ToString());
        opened.EventType.ShouldBe("ledger.agent_wallet_opened");
        return (owner, agentId, new AccountId(opened.Payload.GetProperty("walletId").GetGuid()));
    }

    private async Task<ReadMessage> MoveAsync(Customer owner, string direction, long paisa, int nth = 1, Guid? id = null)
    {
        var transferId = id ?? Guid.CreateVersion7();
        return await SendAsync(new MoveAgentFloat(transferId, transferId, owner.Id.Value, direction, paisa), transferId.ToString(), nth);
    }

    [Fact]
    public async Task Opening_an_agent_wallet_twice_gives_the_same_float_which_the_owner_can_read()
    {
        var (owner, agentId, agentFloat) = await AgentAsync();

        var again = await SendAsync(new OpenAgentWallet(agentId, owner.Id.Value, "WPKA000001", "Bismillah Easy Cash"), agentId.ToString(), nth: 2);
        again.Payload.GetProperty("walletId").GetGuid().ShouldBe(agentFloat.Value);
        agentFloat.ShouldNotBe(owner.WalletId);

        using var response = await owner.Client.GetAsync(new Uri("/api/wallets/me/agent", UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var wallet = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement;
        wallet.GetProperty("walletId").GetGuid().ShouldBe(agentFloat.Value);
        wallet.GetProperty("type").GetString().ShouldBe("AgentWallet");
    }

    [Fact]
    public async Task Float_cash_in_cash_out_and_commission_payout_all_balance()
    {
        var (owner, agentId, agentFloat) = await AgentAsync(walletRupees: 5_000);
        var customer = await factory.CustomerAsync(rupees: 0);
        var expenseBefore = await factory.SystemBalanceAsync(ChartOfAccounts.CommissionExpense);
        var payableBefore = await factory.SystemBalanceAsync(ChartOfAccounts.CommissionPayable);
        var feeBefore = await factory.SystemBalanceAsync(ChartOfAccounts.FeeIncome);
        var taxBefore = await factory.SystemBalanceAsync(ChartOfAccounts.TaxPayable);

        // The agent loads Rs. 5,000 of float from its own wallet.
        var loaded = await MoveAsync(owner, MoveAgentFloat.ToFloat, 500_000);
        loaded.EventType.ShouldBe("ledger.agent_float_moved");
        loaded.Payload.GetProperty("agentBalancePaisa").GetInt64().ShouldBe(500_000);
        (await factory.BalanceAsync(owner.WalletId)).ShouldBe(0);

        // Cash-in Rs. 2,000 (commission Rs. 8), sent twice: posted once.
        var cashInId = Guid.CreateVersion7();
        AgentCashIn CashIn() => new(cashInId, cashInId, owner.Id.Value, customer.Phone, 200_000, 800, "Cash deposit at Bismillah Easy Cash");
        var cashedIn = await SendAsync(CashIn(), cashInId.ToString());
        var repeat = await SendAsync(CashIn(), cashInId.ToString(), nth: 2);

        cashedIn.EventType.ShouldBe("ledger.agent_cashed_in");
        cashedIn.Payload.GetProperty("customerUserId").GetGuid().ShouldBe(customer.Id.Value);
        cashedIn.Payload.GetProperty("agentBalancePaisa").GetInt64().ShouldBe(300_000);
        repeat.EventType.ShouldBe("ledger.agent_cashed_in");
        repeat.Payload.GetProperty("agentBalancePaisa").GetInt64().ShouldBe(300_000);
        (await factory.BalanceAsync(customer.WalletId)).ShouldBe(200_000);

        // Cash-out Rs. 1,000: fee Rs. 10 and tax Rs. 1.60 on top; the agent's float gets the Rs. 1,000 it hands over.
        var cashOutId = Guid.CreateVersion7();
        var cashedOut = await SendAsync(new AgentCashOut(cashOutId, cashOutId, owner.Id.Value, customer.Id.Value, 100_000, 1_000, 160, 600, "Cash withdrawal at Bismillah Easy Cash", "device-1"), cashOutId.ToString());

        cashedOut.EventType.ShouldBe("ledger.agent_cashed_out");
        cashedOut.Payload.GetProperty("agentBalancePaisa").GetInt64().ShouldBe(400_000);
        (await factory.BalanceAsync(customer.WalletId)).ShouldBe(200_000 - 101_160);
        (await factory.SystemBalanceAsync(ChartOfAccounts.FeeIncome) - feeBefore).ShouldBe(1_000);
        (await factory.SystemBalanceAsync(ChartOfAccounts.TaxPayable) - taxBefore).ShouldBe(160);
        (await factory.SystemBalanceAsync(ChartOfAccounts.CommissionExpense) - expenseBefore).ShouldBe(1_400);
        (await factory.SystemBalanceAsync(ChartOfAccounts.CommissionPayable) - payableBefore).ShouldBe(1_400);

        // The commissions are paid to the float, less 12% withholding tax.
        var payoutId = Guid.CreateVersion7();
        PayAgentCommission Pay() => new(payoutId, agentId, owner.Id.Value, 1_400, 168, "Commission 2026-11-19");
        var paid = await SendAsync(Pay(), payoutId.ToString());
        await SendAsync(Pay(), payoutId.ToString(), nth: 2);

        paid.EventType.ShouldBe("ledger.agent_commission_paid");
        paid.Payload.GetProperty("netPaisa").GetInt64().ShouldBe(1_232);
        (await factory.BalanceAsync(agentFloat)).ShouldBe(401_232);
        (await factory.SystemBalanceAsync(ChartOfAccounts.CommissionPayable) - payableBefore).ShouldBe(0);
        (await factory.SystemBalanceAsync(ChartOfAccounts.TaxPayable) - taxBefore).ShouldBe(328);

        // Float back to the agent's wallet.
        var back = await MoveAsync(owner, MoveAgentFloat.ToWallet, 401_232);
        back.EventType.ShouldBe("ledger.agent_float_moved");
        (await factory.BalanceAsync(owner.WalletId)).ShouldBe(401_232);
        (await factory.BalanceAsync(agentFloat)).ShouldBe(0);
        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task A_cash_in_beyond_the_float_is_refused_for_good()
    {
        var (owner, _, _) = await AgentAsync(walletRupees: 1_000);
        var customer = await factory.CustomerAsync(rupees: 0);
        var id = Guid.CreateVersion7();
        AgentCashIn CashIn() => new(id, id, owner.Id.Value, customer.Phone, 50_000, 200, "Cash deposit");

        var refused = await SendAsync(CashIn(), id.ToString());
        await MoveAsync(owner, MoveAgentFloat.ToFloat, 100_000);
        var late = await SendAsync(CashIn(), id.ToString(), nth: 2);

        refused.EventType.ShouldBe("ledger.funds_transfer_rejected");
        refused.Payload.GetProperty("code").GetString().ShouldBe("agent.float_insufficient");
        late.EventType.ShouldBe("ledger.funds_transfer_rejected");
        (await factory.BalanceAsync(customer.WalletId)).ShouldBe(0);
    }

    [Fact]
    public async Task Cash_in_to_an_unknown_or_the_agents_own_number_and_cash_out_beyond_the_balance_are_refused()
    {
        var (owner, _, _) = await AgentAsync(walletRupees: 1_000);
        await MoveAsync(owner, MoveAgentFloat.ToFloat, 100_000);
        var poor = await factory.CustomerAsync(rupees: 100);

        var unknown = Guid.CreateVersion7();
        (await SendAsync(new AgentCashIn(unknown, unknown, owner.Id.Value, NewPhone(), 10_000, 0, "Cash deposit"), unknown.ToString()))
            .Payload.GetProperty("code").GetString().ShouldBe("wallet.recipient_not_found");

        var own = Guid.CreateVersion7();
        (await SendAsync(new AgentCashIn(own, own, owner.Id.Value, owner.Phone, 10_000, 0, "Cash deposit"), own.ToString()))
            .Payload.GetProperty("code").GetString().ShouldBe("agent.own_wallet");

        var tooMuch = Guid.CreateVersion7();
        (await SendAsync(new AgentCashOut(tooMuch, tooMuch, owner.Id.Value, poor.Id.Value, 10_000, 100, 16, 60, "Cash withdrawal", null), tooMuch.ToString()))
            .Payload.GetProperty("code").GetString().ShouldBe("wallet.insufficient_balance");
        (await factory.BalanceAsync(poor.WalletId)).ShouldBe(10_000);
    }
}
