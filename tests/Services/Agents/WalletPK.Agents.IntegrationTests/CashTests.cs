using System.Net;
using System.Net.Http.Json;
using WalletPK.Agents.IntegrationTests.Infrastructure;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using static WalletPK.Agents.IntegrationTests.Infrastructure.AgentSteps;

namespace WalletPK.Agents.IntegrationTests;

/// <summary>M9.2 and M9.3: cash-in from the float, and cash-out with the customer's one-time code.</summary>
[Collection(AgentsCollection.Name)]
public class CashTests(AgentsFactory factory) : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        factory.Clock.Reset();
        return ValueTask.CompletedTask;
    }

    private static async Task<HttpResponseMessage> RequestCashOutAsync(TestUser customer, string agentCode, long amount) =>
        await customer.Client.PostAsJsonAsync(new Uri("/api/agents/cash-outs", UriKind.Relative), new { agentCode, amountPaisa = amount }, Cancel);

    private static async Task<(Guid Id, string Code)> CashOutCodeAsync(TestUser customer, string agentCode, long amount)
    {
        using var response = await RequestCashOutAsync(customer, agentCode, amount);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
        var request = await JsonAsync(response);
        return (request.GetProperty("id").GetGuid(), request.GetProperty("code").GetString()!);
    }

    private static Task<HttpResponseMessage> TakeAsync(TestAgent agent, TestUser customer, string code, string? key = null) =>
        PostAsync(agent.Client, "/api/agents/me/cash-outs", new { customerNumber = customer.Phone, code }, key ?? Guid.NewGuid().ToString());

    private static string Wrong(string code) => code == "000000" ? "111111" : "000000";

    [Fact]
    public async Task Cash_in_credits_the_customer_from_the_float_with_the_agents_commission()
    {
        using var agent = await factory.ActiveAgentAsync(floatPaisa: 600_000);
        using var customer = factory.Customer();

        // Rs. 2,000 in (0.4%: Rs. 8 commission); the float falls to Rs. 4,000, below the Rs. 5,000 alert level.
        var local = $"0{customer.Phone[3..]}";
        var key = Guid.NewGuid().ToString();
        var id = await StartAsync(agent.Client, "/api/agents/me/cash-ins", new { customerNumber = local, amountPaisa = 200_000 }, key);
        var done = await WaitForTransactionAsync(agent.Client, id);

        done.GetProperty("status").GetString().ShouldBe("Completed");
        done.GetProperty("kind").GetString().ShouldBe("CashIn");
        done.GetProperty("commissionPaisa").GetInt64().ShouldBe(800);
        done.GetProperty("feePaisa").GetInt64().ShouldBe(0);
        done.GetProperty("agentBalancePaisa").GetInt64().ShouldBe(400_000);
        done.GetProperty("customerName").GetString()!.ShouldContain("•••");
        factory.Ledger.Wallet(customer.Id).ShouldBe(200_000);
        var command = factory.Ledger.CommandsFor(id).OfType<AgentCashIn>().ShouldHaveSingleItem();
        command.CustomerWalletNumber.ShouldBe(customer.Phone);
        command.Description.ShouldContain(agent.Code);

        // The customer sees it too, and a retry with the same key is the same cash-in.
        var mine = await GetAsync(customer.Client, "/api/agents/transactions");
        mine.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldContain(id);
        (await GetAsync(customer.Client, $"/api/agents/transactions/{id}")).GetProperty("amountPaisa").GetInt64().ShouldBe(200_000);
        (await StartAsync(agent.Client, "/api/agents/me/cash-ins", new { customerNumber = local, amountPaisa = 200_000 }, key)).ShouldBe(id);
        factory.Ledger.CommandsFor(id).OfType<AgentCashIn>().Count().ShouldBe(1);

        var events = factory.ReadUntil(KafkaTopics.AgentsEvents, read =>
            read.Any(e => e.EventType == "agents.float_low" && e.Payload.GetProperty("agentId").GetGuid() == agent.Id)
            && read.Any(e => e.EventType == "agents.transaction_changed" && e.Payload.GetProperty("transactionId").GetGuid() == id));
        events.Single(e => e.EventType == "agents.float_low" && e.Payload.GetProperty("agentId").GetGuid() == agent.Id).Payload.GetProperty("floatPaisa").GetInt64().ShouldBe(400_000);

        var me = await GetAsync(agent.Client, "/api/agents/me");
        me.GetProperty("unpaidCommissionPaisa").GetInt64().ShouldBe(800);
        (await GetAsync(agent.Client, "/api/agents/me/transactions"))[0].GetProperty("id").GetGuid().ShouldBe(id);
    }

    [Theory]
    [InlineData("12345", 10_000, "agent.invalid_number")]
    [InlineData(null, 5_000, "agent.invalid_amount")]
    [InlineData(null, 10_050, "agent.invalid_amount")]
    [InlineData(null, 6_000_000, "agent.invalid_amount")]
    public async Task Invalid_cash_ins_are_refused(string? number, long amount, string code)
    {
        using var agent = await factory.ActiveAgentAsync();
        using var customer = factory.Customer();

        using var response = await PostAsync(agent.Client, "/api/agents/me/cash-ins", new { customerNumber = number ?? customer.Phone, amountPaisa = amount }, Guid.NewGuid().ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).ShouldBe(code);
    }

    [Fact]
    public async Task A_cash_in_to_the_agents_own_number_beyond_the_float_or_to_no_wallet_is_refused()
    {
        using var agent = await factory.ActiveAgentAsync(floatPaisa: 50_000);
        using var customer = factory.Customer();

        using (var own = await PostAsync(agent.Client, "/api/agents/me/cash-ins", new { customerNumber = agent.Owner.Phone, amountPaisa = 10_000 }, Guid.NewGuid().ToString()))
        {
            (await ErrorCodeAsync(own)).ShouldBe("agent.own_wallet");
        }

        var tooMuch = await StartAsync(agent.Client, "/api/agents/me/cash-ins", new { customerNumber = customer.Phone, amountPaisa = 60_000 });
        (await WaitForTransactionAsync(agent.Client, tooMuch)).GetProperty("failureCode").GetString().ShouldBe("agent.float_insufficient");

        var nobody = await StartAsync(agent.Client, "/api/agents/me/cash-ins", new { customerNumber = AgentsFactory.NewPhone(), amountPaisa = 10_000 });
        (await WaitForTransactionAsync(agent.Client, nobody)).GetProperty("failureCode").GetString().ShouldBe("wallet.recipient_not_found");
        factory.Ledger.Float(agent.Owner.Id).ShouldBe(50_000);
    }

    [Fact]
    public async Task A_cash_in_is_sent_again_while_the_ledger_is_silent_and_posted_once()
    {
        using var agent = await factory.ActiveAgentAsync(floatPaisa: 100_000);
        using var customer = factory.Customer();
        factory.Ledger.IgnoreNext(agent.Owner.Id, 1);

        var id = await StartAsync(agent.Client, "/api/agents/me/cash-ins", new { customerNumber = customer.Phone, amountPaisa = 30_000 });
        (await WaitForTransactionAsync(agent.Client, id)).GetProperty("status").GetString().ShouldBe("Completed");

        factory.Ledger.CommandsFor(id).OfType<AgentCashIn>().Count().ShouldBeGreaterThanOrEqualTo(2);
        factory.Ledger.Wallet(customer.Id).ShouldBe(30_000);
    }

    [Fact]
    public async Task Cash_out_takes_the_customers_code_once_with_the_fee_on_top()
    {
        using var agent = await factory.ActiveAgentAsync();
        using var customer = factory.Customer(paisa: 200_000);

        // Rs. 1,000 cash: fee Rs. 10 (1%, min Rs. 10), tax Rs. 1.60, the agent earns 60% of the fee.
        using var created = await RequestCashOutAsync(customer, agent.Code, 100_000);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var request = await JsonAsync(created);
        var code = request.GetProperty("code").GetString()!;
        code.ShouldMatch("^[0-9]{6}$");
        request.GetProperty("totalPaisa").GetInt64().ShouldBe(101_160);
        request.GetProperty("agentName").GetString().ShouldBe("Bismillah Easy Cash");

        var current = await GetAsync(customer.Client, "/api/agents/cash-outs/current");
        current.GetProperty("status").GetString().ShouldBe("Open");
        current.GetProperty("code").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        using (var wrong = await TakeAsync(agent, customer, Wrong(code)))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ErrorCodeAsync(wrong)).ShouldBe("agent.wrong_code");
        }

        using var taken = await TakeAsync(agent, customer, code);
        taken.StatusCode.ShouldBe(HttpStatusCode.Accepted, await taken.Content.ReadAsStringAsync(Cancel));
        var id = (await JsonAsync(taken)).GetProperty("id").GetGuid();
        var done = await WaitForTransactionAsync(agent.Client, id);

        done.GetProperty("status").GetString().ShouldBe("Completed");
        done.GetProperty("feePaisa").GetInt64().ShouldBe(1_000);
        done.GetProperty("taxPaisa").GetInt64().ShouldBe(160);
        done.GetProperty("commissionPaisa").GetInt64().ShouldBe(600);
        done.GetProperty("agentBalancePaisa").GetInt64().ShouldBe(100_000);
        factory.Ledger.Wallet(customer.Id).ShouldBe(200_000 - 101_160);
        factory.Ledger.CommandsFor(id).OfType<AgentCashOut>().ShouldHaveSingleItem().CustomerUserId.ShouldBe(customer.Id);
        (await GetAsync(customer.Client, "/api/agents/cash-outs/current")).GetProperty("status").GetString().ShouldBe("Completed");

        using var twice = await TakeAsync(agent, customer, code);
        twice.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(twice)).ShouldBe("agent.no_cash_out_request");
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_request()
    {
        using var agent = await factory.ActiveAgentAsync();
        using var customer = factory.Customer(paisa: 200_000);
        var (_, code) = await CashOutCodeAsync(customer, agent.Code, 50_000);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var wrong = await TakeAsync(agent, customer, Wrong(code));
            (await JsonAsync(wrong)).GetProperty("detail").GetString()!.ShouldContain($"{5 - attempt} ");
        }

        using (var fifth = await TakeAsync(agent, customer, Wrong(code)))
        {
            fifth.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await ErrorCodeAsync(fifth)).ShouldBe("agent.code_locked");
        }

        using (var right = await TakeAsync(agent, customer, code))
        {
            (await ErrorCodeAsync(right)).ShouldBe("agent.no_cash_out_request");
        }

        (await GetAsync(customer.Client, "/api/agents/cash-outs/current")).GetProperty("status").GetString().ShouldBe("Locked");
        factory.Ledger.Wallet(customer.Id).ShouldBe(200_000);
    }

    [Fact]
    public async Task A_code_expires_after_15_minutes()
    {
        using var agent = await factory.ActiveAgentAsync();
        using var customer = factory.Customer(paisa: 200_000);
        var (_, code) = await CashOutCodeAsync(customer, agent.Code, 50_000);

        factory.Clock.Advance(TimeSpan.FromMinutes(16));

        using var late = await TakeAsync(agent, customer, code);
        (await ErrorCodeAsync(late)).ShouldBe("agent.no_cash_out_request");
        (await GetAsync(customer.Client, "/api/agents/cash-outs/current")).GetProperty("status").GetString().ShouldBe("Expired");
    }

    [Fact]
    public async Task A_cash_out_the_customer_cannot_cover_fails_and_the_code_works_once_they_can()
    {
        using var agent = await factory.ActiveAgentAsync();
        using var customer = factory.Customer(paisa: 50_000);
        var (_, code) = await CashOutCodeAsync(customer, agent.Code, 50_000);

        using (var first = await TakeAsync(agent, customer, code))
        {
            var failed = await WaitForTransactionAsync(agent.Client, (await JsonAsync(first)).GetProperty("id").GetGuid());
            failed.GetProperty("status").GetString().ShouldBe("Failed");
            failed.GetProperty("failureCode").GetString().ShouldBe("wallet.insufficient_balance");
        }

        var current = await GetAsync(customer.Client, "/api/agents/cash-outs/current");
        current.GetProperty("status").GetString().ShouldBe("Failed");
        current.GetProperty("failureReason").GetString()!.ShouldContain("not have enough");

        factory.Ledger.Deposit(customer.Id, 10_000);
        using var second = await TakeAsync(agent, customer, code);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await WaitForTransactionAsync(agent.Client, (await JsonAsync(second)).GetProperty("id").GetGuid())).GetProperty("status").GetString().ShouldBe("Completed");
        factory.Ledger.Wallet(customer.Id).ShouldBe(60_000 - 50_000 - 1_000 - 160);
    }

    [Fact]
    public async Task Two_agents_tries_with_the_same_code_at_once_pay_out_once()
    {
        using var agent = await factory.ActiveAgentAsync();
        using var customer = factory.Customer(paisa: 500_000);
        var (_, code) = await CashOutCodeAsync(customer, agent.Code, 100_000);

        var responses = await Task.WhenAll(TakeAsync(agent, customer, code), TakeAsync(agent, customer, code));
        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
            var winner = responses.Single(response => response.StatusCode == HttpStatusCode.Accepted);
            await WaitForTransactionAsync(agent.Client, (await JsonAsync(winner)).GetProperty("id").GetGuid());
            factory.Ledger.Wallet(customer.Id).ShouldBe(500_000 - 101_160);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_new_request_replaces_the_open_one_and_requests_can_be_cancelled()
    {
        using var agent = await factory.ActiveAgentAsync();
        using var other = await factory.ActiveAgentAsync(shop: "Other Shop");
        using var customer = factory.Customer(paisa: 500_000);

        var (firstId, firstCode) = await CashOutCodeAsync(customer, agent.Code, 50_000);
        var (secondId, _) = await CashOutCodeAsync(customer, other.Code, 20_000);

        using (var stale = await TakeAsync(agent, customer, firstCode))
        {
            (await ErrorCodeAsync(stale)).ShouldBe("agent.no_cash_out_request");
        }

        using (var cancelled = await PostAsync(customer.Client, $"/api/agents/cash-outs/{secondId}/cancel", null))
        {
            cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await JsonAsync(cancelled)).GetProperty("status").GetString().ShouldBe("Cancelled");
        }

        using (var again = await PostAsync(customer.Client, $"/api/agents/cash-outs/{firstId}/cancel", null))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var own = await RequestCashOutAsync(agent.Owner, agent.Code, 10_000))
        {
            (await ErrorCodeAsync(own)).ShouldBe("agent.own_wallet");
        }

        using (var unknown = await RequestCashOutAsync(customer, "WPKA000000", 10_000))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Theory]
    [InlineData("CashIn", 100_000, 400, 0, 0)]
    [InlineData("CashOut", 100_000, 600, 1_000, 160)]
    [InlineData("CashOut", 5_000_000, 18_000, 30_000, 4_800)]
    public async Task Quotes_show_the_fee_and_the_commission(string kind, long amount, long commission, long fee, long tax)
    {
        using var customer = factory.Customer();

        var quote = await GetAsync(customer.Client, $"/api/agents/quote?kind={kind}&amountPaisa={amount}");

        quote.GetProperty("commissionPaisa").GetInt64().ShouldBe(commission);
        quote.GetProperty("feePaisa").GetInt64().ShouldBe(fee);
        quote.GetProperty("taxPaisa").GetInt64().ShouldBe(tax);
    }
}
