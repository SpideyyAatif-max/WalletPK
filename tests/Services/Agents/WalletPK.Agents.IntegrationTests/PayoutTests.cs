using System.Globalization;
using System.Net;
using System.Text.Json;
using WalletPK.Agents.IntegrationTests.Infrastructure;
using WalletPK.Messaging;
using static WalletPK.Agents.IntegrationTests.Infrastructure.AgentSteps;

namespace WalletPK.Agents.IntegrationTests;

/// <summary>M9.4: commissions accrue per transaction and are paid to the float nightly, less withholding tax.</summary>
[Collection(AgentsCollection.Name)]
public class PayoutTests(AgentsFactory factory) : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        factory.Clock.Reset();
        return ValueTask.CompletedTask;
    }

    private string Today() => DateOnly.FromDateTime(factory.Clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5)).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<JsonElement> RunAsync(HttpClient ops)
    {
        using var run = await PostAsync(ops, $"/api/agents/ops/payouts/run?date={Today()}", null);
        run.StatusCode.ShouldBe(HttpStatusCode.OK, await run.Content.ReadAsStringAsync(Cancel));
        return await JsonAsync(run);
    }

    /// <summary>The agent's newest payout, once it has <paramref name="status"/>.</summary>
    private static async Task<JsonElement> WaitForPayoutAsync(TestAgent agent, string status) =>
        (await WaitAsync(
            () => GetAsync(agent.Client, "/api/agents/me/payouts"),
            payouts => payouts.GetArrayLength() > 0 && payouts[0].GetProperty("status").GetString() == status,
            $"{status} payout"))[0];

    [Fact]
    public async Task Commissions_are_paid_to_each_agents_float_once_less_withholding_and_a_refused_payout_is_retried_the_next_night()
    {
        using var shop = await factory.ActiveAgentAsync(floatPaisa: 1_000_000, shop: "Madina Cash Point");
        using var kiosk = await factory.ActiveAgentAsync(floatPaisa: 1_000_000, shop: "Ghazi Easy Paisa");
        using var customer = factory.Customer(paisa: 1_000_000);
        using var ops = factory.Operator();

        // Shop: cash-in Rs. 5,000 (Rs. 20) and a cash-out of Rs. 2,000 (60% of Rs. 20 fee = Rs. 12). Kiosk: cash-in Rs. 1,000 (Rs. 4).
        await WaitForTransactionAsync(shop.Client, await StartAsync(shop.Client, "/api/agents/me/cash-ins", new { customerNumber = customer.Phone, amountPaisa = 500_000 }));
        using (var request = await PostAsync(customer.Client, "/api/agents/cash-outs", new { agentCode = shop.Code, amountPaisa = 200_000 }))
        {
            var code = (await JsonAsync(request)).GetProperty("code").GetString()!;
            await WaitForTransactionAsync(shop.Client, await StartAsync(shop.Client, "/api/agents/me/cash-outs", new { customerNumber = customer.Phone, code }));
        }

        await WaitForTransactionAsync(kiosk.Client, await StartAsync(kiosk.Client, "/api/agents/me/cash-ins", new { customerNumber = customer.Phone, amountPaisa = 100_000 }));
        (await GetAsync(shop.Client, "/api/agents/me")).GetProperty("unpaidCommissionPaisa").GetInt64().ShouldBe(3_200);

        // Tonight the kiosk's payout is refused.
        factory.Ledger.RefusePayouts(kiosk.Id);
        (await RunAsync(ops)).GetProperty("payouts").GetInt32().ShouldBeGreaterThanOrEqualTo(2);

        // Rs. 32 gross, 12% withheld (Rs. 3.84): Rs. 28.16 to the float.
        var paid = await WaitForPayoutAsync(shop, "Paid");
        paid.GetProperty("transactionCount").GetInt32().ShouldBe(2);
        paid.GetProperty("grossPaisa").GetInt64().ShouldBe(3_200);
        paid.GetProperty("taxPaisa").GetInt64().ShouldBe(384);
        paid.GetProperty("netPaisa").GetInt64().ShouldBe(2_816);
        factory.Ledger.Paid[paid.GetProperty("id").GetGuid()].AgentOwnerUserId.ShouldBe(shop.Owner.Id);
        (await GetAsync(shop.Client, "/api/agents/me")).GetProperty("unpaidCommissionPaisa").GetInt64().ShouldBe(0);

        var refused = await WaitForPayoutAsync(kiosk, "Failed");
        refused.GetProperty("failureReason").GetString()!.ShouldContain("agent.payout_invalid");
        (await GetAsync(kiosk.Client, "/api/agents/me")).GetProperty("unpaidCommissionPaisa").GetInt64().ShouldBe(400);

        // Running the same night again pays nothing twice.
        await RunAsync(ops);
        (await GetAsync(shop.Client, "/api/agents/me/payouts")).GetArrayLength().ShouldBe(1);
        (await GetAsync(kiosk.Client, "/api/agents/me/payouts")).GetArrayLength().ShouldBe(1);

        // The next night the kiosk's commission goes again, and is paid.
        factory.Ledger.AllowPayouts(kiosk.Id);
        factory.Clock.Advance(TimeSpan.FromDays(1));
        await RunAsync(ops);

        var retried = await WaitForPayoutAsync(kiosk, "Paid");
        retried.GetProperty("grossPaisa").GetInt64().ShouldBe(400);
        retried.GetProperty("netPaisa").GetInt64().ShouldBe(352);
        (await GetAsync(kiosk.Client, "/api/agents/me/payouts")).GetArrayLength().ShouldBe(2);

        bool PayoutFor(Guid agentId, (string EventType, JsonElement Payload) e) => e.EventType == "agents.commission_payout_changed" && e.Payload.GetProperty("agentId").GetGuid() == agentId;
        var events = factory.ReadUntil(KafkaTopics.AgentsEvents, read => read.Count(e => PayoutFor(kiosk.Id, e)) >= 2 && read.Any(e => PayoutFor(shop.Id, e)));
        events.Where(e => PayoutFor(kiosk.Id, e)).Select(e => e.Payload.GetProperty("status").GetString()).ShouldBe(["Failed", "Paid"]);
    }
}
