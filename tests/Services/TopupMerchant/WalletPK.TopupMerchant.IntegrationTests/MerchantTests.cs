using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using WalletPK.TopupMerchant.IntegrationTests.Infrastructure;

namespace WalletPK.TopupMerchant.IntegrationTests;

/// <summary>M8.2-M8.5: merchant onboarding, signed dynamic QR codes, QR payments with the fee split off, and settlement.</summary>
[Collection(TopupMerchantCollection.Name)]
public class MerchantTests(TopupMerchantFactory factory) : IAsyncLifetime
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        factory.Clock.Reset();
        return ValueTask.CompletedTask;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        return await JsonAsync(response);
    }

    /// <summary>Registers a business for a new owner and waits until the Ledger has opened its wallet.</summary>
    private async Task<(Guid Owner, HttpClient Client, JsonElement Merchant)> ActiveMerchantAsync(string category = "RETAIL", string name = "Ali Tea Stall")
    {
        var owner = Guid.NewGuid();
        var client = factory.ClientFor(owner);
        using (var response = await client.PostAsJsonAsync(new Uri("/api/merchants", UriKind.Relative), new { name, category, city = "Lahore" }, Cancel))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var merchant = await GetAsync(client, "/api/merchants/me");
            if (merchant.GetProperty("status").GetString() == "Active")
            {
                return (owner, client, merchant);
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Merchant did not become active: {merchant}");
            }

            await Task.Delay(100, Cancel);
        }
    }

    private static async Task<JsonElement> CreateQrAsync(HttpClient merchant, long amount, string? reference = "INV-42")
    {
        using var response = await merchant.PostAsJsonAsync(new Uri("/api/merchants/me/qr", UriKind.Relative), new { amountPaisa = amount, reference }, Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
        return await JsonAsync(response);
    }

    private static async Task<HttpResponseMessage> PayAsync(HttpClient payer, string payload, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/merchants/qr/pay") { Content = JsonContent.Create(new { payload }) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await payer.SendAsync(request, Cancel);
    }

    private static async Task<Guid> PayOkAsync(HttpClient payer, string payload)
    {
        using var response = await PayAsync(payer, payload);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Cancel));
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> WaitForPaymentAsync(HttpClient client, Guid id, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement payment;
        do
        {
            payment = await GetAsync(client, $"/api/merchants/payments/{id}");
            if (payment.GetProperty("status").GetString() == status)
            {
                return payment;
            }

            await Task.Delay(100, Cancel);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Payment {id} did not reach {status}: {payment}");
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("code").GetString()!;

    [Fact]
    public async Task Fee_plans_and_categories_are_listed()
    {
        using var client = factory.ClientFor(Guid.NewGuid());

        var plans = await GetAsync(client, "/api/merchants/fee-plans");

        plans.GetProperty("plans").EnumerateArray().Select(plan => plan.GetProperty("code").GetString()).ShouldBe(["STANDARD", "KIRYANA", "FOOD", "FUEL"]);
        plans.GetProperty("categories").GetArrayLength().ShouldBe(5);
        plans.GetProperty("taxBasisPoints").GetInt32().ShouldBe(1_600);
    }

    [Fact]
    public async Task A_business_is_registered_once_and_becomes_active_when_the_ledger_opens_its_wallet()
    {
        var (owner, client, merchant) = await ActiveMerchantAsync("KIRYANA", "Bismillah Kiryana");
        using var _ = client;

        merchant.GetProperty("code").GetString()!.ShouldMatch("^WPKM[0-9]{6}$");
        merchant.GetProperty("name").GetString().ShouldBe("Bismillah Kiryana");
        merchant.GetProperty("feePlan").GetProperty("code").GetString().ShouldBe("KIRYANA");
        merchant.GetProperty("unsettledPayments").GetInt32().ShouldBe(0);

        var open = factory.Ledger.CommandsFor(merchant.GetProperty("id").GetGuid()).OfType<OpenMerchantWallet>().ShouldHaveSingleItem();
        open.OwnerUserId.ShouldBe(owner);
        open.MerchantCode.ShouldBe(merchant.GetProperty("code").GetString());

        using var again = await client.PostAsJsonAsync(new Uri("/api/merchants", UriKind.Relative), new { name = "Second shop", category = "FOOD" }, Cancel);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(again)).ShouldBe("merchant.already_registered");
    }

    [Theory]
    [InlineData("A", "RETAIL", "merchant.invalid_name")]
    [InlineData("Good name", "CASINO", "merchant.invalid_category")]
    public async Task Invalid_registrations_are_refused(string name, string category, string code)
    {
        using var client = factory.ClientFor(Guid.NewGuid());

        using var response = await client.PostAsJsonAsync(new Uri("/api/merchants", UriKind.Relative), new { name, category }, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).ShouldBe(code);
    }

    [Fact]
    public async Task Someone_without_a_merchant_account_gets_not_found()
    {
        using var client = factory.ClientFor(Guid.NewGuid());

        using var response = await client.GetAsync(new Uri("/api/merchants/me", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_qr_payment_moves_the_money_with_the_fee_and_tax_split_off()
    {
        var (_, merchant, account) = await ActiveMerchantAsync();
        using var _m = merchant;
        var payerId = Guid.NewGuid();
        using var payer = factory.ClientFor(payerId, phone: "+923451234567");

        var qr = await CreateQrAsync(merchant, 100_000);
        qr.GetProperty("status").GetString().ShouldBe("Open");
        var payload = qr.GetProperty("payload").GetString()!;
        payload.ShouldStartWith("WPKQR1.");

        // The payer scans it and sees who and how much.
        using (var preview = await payer.PostAsJsonAsync(new Uri("/api/merchants/qr/preview", UriKind.Relative), new { payload }, Cancel))
        {
            preview.StatusCode.ShouldBe(HttpStatusCode.OK);
            var seen = await JsonAsync(preview);
            seen.GetProperty("merchantName").GetString().ShouldBe("Ali Tea Stall");
            seen.GetProperty("amountPaisa").GetInt64().ShouldBe(100_000);
            seen.GetProperty("reference").GetString().ShouldBe("INV-42");
        }

        var id = await PayOkAsync(payer, payload);
        var payment = await WaitForPaymentAsync(payer, id, "Completed");

        // Standard plan: 1% fee (Rs. 10), 16% tax on it (Rs. 1.60); the merchant gets Rs. 988.40.
        payment.GetProperty("feePaisa").GetInt64().ShouldBe(1_000);
        payment.GetProperty("taxPaisa").GetInt64().ShouldBe(160);
        payment.GetProperty("netPaisa").GetInt64().ShouldBe(98_840);
        var command = factory.Ledger.Paid[id];
        command.PayerUserId.ShouldBe(payerId);
        command.MerchantOwnerUserId.ShouldNotBe(payerId);
        (command.AmountPaisa, command.FeePaisa, command.TaxPaisa).ShouldBe((100_000L, 1_000L, 160L));

        // The till sees it paid; the merchant's list shows the payer's number masked.
        var till = await GetAsync(merchant, $"/api/merchants/me/qr/{qr.GetProperty("id").GetGuid()}");
        till.GetProperty("status").GetString().ShouldBe("Paid");
        till.GetProperty("paymentId").GetGuid().ShouldBe(id);
        var sales = await GetAsync(merchant, "/api/merchants/me/payments");
        sales[0].GetProperty("id").GetGuid().ShouldBe(id);
        sales[0].GetProperty("payerWalletNumber").GetString().ShouldBe("+92345•••4567");

        var me = await GetAsync(merchant, "/api/merchants/me");
        me.GetProperty("unsettledPayments").GetInt32().ShouldBe(1);
        me.GetProperty("unsettledNetPaisa").GetInt64().ShouldBe(98_840);

        var mine = await GetAsync(payer, "/api/merchants/payments");
        mine.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldContain(id);

        var events = factory.ReadUntil(KafkaTopics.TopupEvents, read => read.Any(e => e.EventType == "topup.merchant_payment_changed" && e.Payload.GetProperty("paymentId").GetGuid() == id));
        events.Single(e => e.EventType == "topup.merchant_payment_changed" && e.Payload.GetProperty("paymentId").GetGuid() == id)
            .Payload.GetProperty("merchantId").GetGuid().ShouldBe(account.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_qr_code_is_paid_once_even_when_two_people_pay_at_the_same_time()
    {
        var (_, merchant, _) = await ActiveMerchantAsync();
        using var _m = merchant;
        var payload = (await CreateQrAsync(merchant, 50_000)).GetProperty("payload").GetString()!;
        using var first = factory.ClientFor(Guid.NewGuid());
        using var second = factory.ClientFor(Guid.NewGuid());

        var responses = await Task.WhenAll(PayAsync(first, payload), PayAsync(second, payload));
        try
        {
            responses.Select(response => response.StatusCode).ShouldBe([HttpStatusCode.Accepted, HttpStatusCode.Conflict], ignoreOrder: true);
            var loser = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
            (await ErrorCodeAsync(loser)).ShouldBe("merchant.qr_paid");
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
    public async Task Paying_again_with_the_same_key_returns_the_same_payment()
    {
        var (_, merchant, _) = await ActiveMerchantAsync();
        using var _m = merchant;
        var payload = (await CreateQrAsync(merchant, 20_000)).GetProperty("payload").GetString()!;
        using var payer = factory.ClientFor(Guid.NewGuid());
        var key = Guid.NewGuid().ToString();

        using var first = await PayAsync(payer, payload, key);
        var id = (await JsonAsync(first)).GetProperty("id").GetGuid();
        await WaitForPaymentAsync(payer, id, "Completed");

        using var again = await PayAsync(payer, payload, key);
        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await JsonAsync(again)).GetProperty("id").GetGuid().ShouldBe(id);
        factory.Ledger.CommandsFor(id).OfType<PayMerchant>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_changed_an_own_or_an_expired_qr_code_cannot_be_paid()
    {
        var (_, merchant, _) = await ActiveMerchantAsync();
        using var _m = merchant;
        using var payer = factory.ClientFor(Guid.NewGuid());
        var payload = (await CreateQrAsync(merchant, 10_000)).GetProperty("payload").GetString()!;

        // A smaller amount written into the payload: the signature no longer matches.
        var parts = payload.Split('.');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight(parts[1].Length + ((4 - (parts[1].Length % 4)) % 4), '=')));
        var tampered = $"{parts[0]}.{Convert.ToBase64String(Encoding.UTF8.GetBytes(json.Replace("\"a\":10000", "\"a\":100", StringComparison.Ordinal))).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.{parts[2]}";
        tampered.ShouldNotBe(payload);
        using (var response = await PayAsync(payer, tampered))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ErrorCodeAsync(response)).ShouldBe("merchant.invalid_qr");
        }

        using (var response = await PayAsync(payer, "not a qr code"))
        {
            (await ErrorCodeAsync(response)).ShouldBe("merchant.invalid_qr");
        }

        using (var response = await PayAsync(merchant, payload))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ErrorCodeAsync(response)).ShouldBe("merchant.own_qr");
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        using (var response = await PayAsync(payer, payload))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await ErrorCodeAsync(response)).ShouldBe("merchant.qr_expired");
        }
    }

    [Theory]
    [InlineData(0L, null, "merchant.invalid_amount")]
    [InlineData(60_000_000L, null, "merchant.invalid_amount")]
    [InlineData(10_000L, 30, "merchant.invalid_expiry")]
    public async Task Invalid_qr_codes_are_refused(long amount, int? expiresInSeconds, string code)
    {
        var (_, merchant, _) = await ActiveMerchantAsync();
        using var _m = merchant;

        using var response = await merchant.PostAsJsonAsync(new Uri("/api/merchants/me/qr", UriKind.Relative), new { amountPaisa = amount, expiresInSeconds }, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).ShouldBe(code);
    }

    [Fact]
    public async Task A_refused_payment_fails_and_the_qr_code_can_be_paid_by_someone_else()
    {
        var (_, merchant, _) = await ActiveMerchantAsync();
        using var _m = merchant;
        var qr = await CreateQrAsync(merchant, 30_000);
        var payload = qr.GetProperty("payload").GetString()!;
        var brokeId = Guid.NewGuid();
        factory.Ledger.Refuse(brokeId, "wallet.insufficient_balance", "Not enough money in your wallet.");
        using var broke = factory.ClientFor(brokeId);
        using var other = factory.ClientFor(Guid.NewGuid());

        var failedId = await PayOkAsync(broke, payload);
        var failed = await WaitForPaymentAsync(broke, failedId, "Failed");
        failed.GetProperty("failureCode").GetString().ShouldBe("wallet.insufficient_balance");
        (await GetAsync(merchant, $"/api/merchants/me/qr/{qr.GetProperty("id").GetGuid()}")).GetProperty("status").GetString().ShouldBe("Open");

        var paidId = await PayOkAsync(other, payload);
        await WaitForPaymentAsync(other, paidId, "Completed");
        (await GetAsync(merchant, $"/api/merchants/me/qr/{qr.GetProperty("id").GetGuid()}")).GetProperty("paymentId").GetGuid().ShouldBe(paidId);
    }

    [Fact]
    public async Task A_payment_is_sent_again_while_the_ledger_is_silent_and_posted_once()
    {
        var (_, merchant, _) = await ActiveMerchantAsync();
        using var _m = merchant;
        var payerId = Guid.NewGuid();
        factory.Ledger.IgnoreNext(payerId, 1);
        using var payer = factory.ClientFor(payerId);

        var id = await PayOkAsync(payer, (await CreateQrAsync(merchant, 15_000)).GetProperty("payload").GetString()!);
        await WaitForPaymentAsync(payer, id, "Completed");

        factory.Ledger.CommandsFor(id).OfType<PayMerchant>().Count().ShouldBeGreaterThanOrEqualTo(2);
        factory.Ledger.Paid.ContainsKey(id).ShouldBeTrue();
    }

    [Fact]
    public async Task Settlement_sweeps_each_merchants_completed_sales_once_and_a_refused_one_is_retried_the_next_night()
    {
        var (_, shop, shopAccount) = await ActiveMerchantAsync("FOOD", "Karachi Biryani");
        var (_, pump, pumpAccount) = await ActiveMerchantAsync("FUEL", "Shell Pump");
        using var _s = shop;
        using var _p = pump;
        using var payer = factory.ClientFor(Guid.NewGuid());
        using var ops = factory.ClientFor(Guid.NewGuid(), role: "finance");
        var shopId = shopAccount.GetProperty("id").GetGuid();
        var pumpId = pumpAccount.GetProperty("id").GetGuid();

        foreach (var (merchant, amount) in new[] { (shop, 100_000L), (shop, 50_000L), (pump, 500_000L) })
        {
            var id = await PayOkAsync(payer, (await CreateQrAsync(merchant, amount)).GetProperty("payload").GetString()!);
            await WaitForPaymentAsync(payer, id, "Completed");
        }

        // The pump's bank refuses tonight.
        factory.Ledger.RefuseSettlements(pumpId);
        using (var run = await ops.PostAsync(new Uri("/api/merchants/ops/settlements/run?date=" + Today(), UriKind.Relative), null, Cancel))
        {
            run.StatusCode.ShouldBe(HttpStatusCode.OK, await run.Content.ReadAsStringAsync(Cancel));
            (await JsonAsync(run)).GetProperty("settlements").GetInt32().ShouldBeGreaterThanOrEqualTo(2);
        }

        // Food plan: 0.8% (min Rs. 1): Rs. 8 + Rs. 4 in fees, 16% tax on each.
        var shopSettlement = await WaitForSettlementAsync(shop, "Settled");
        shopSettlement.GetProperty("paymentCount").GetInt32().ShouldBe(2);
        shopSettlement.GetProperty("grossPaisa").GetInt64().ShouldBe(150_000);
        shopSettlement.GetProperty("feePaisa").GetInt64().ShouldBe(1_200);
        shopSettlement.GetProperty("taxPaisa").GetInt64().ShouldBe(192);
        shopSettlement.GetProperty("netPaisa").GetInt64().ShouldBe(148_608);
        factory.Ledger.Settled[shopSettlement.GetProperty("id").GetGuid()].AmountPaisa.ShouldBe(148_608);
        (await GetAsync(shop, "/api/merchants/me")).GetProperty("unsettledPayments").GetInt32().ShouldBe(0);

        var refused = await WaitForSettlementAsync(pump, "Failed");
        refused.GetProperty("failureReason").GetString()!.ShouldContain("merchant.settlement_invalid");
        (await GetAsync(pump, "/api/merchants/me")).GetProperty("unsettledPayments").GetInt32().ShouldBe(1);

        // Running the same day again settles nothing twice.
        using (var rerun = await ops.PostAsync(new Uri("/api/merchants/ops/settlements/run?date=" + Today(), UriKind.Relative), null, Cancel))
        {
            rerun.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await GetAsync(shop, "/api/merchants/me/settlements")).GetArrayLength().ShouldBe(1);
        (await GetAsync(pump, "/api/merchants/me/settlements")).GetArrayLength().ShouldBe(1);

        // The next night the pump's payment goes again, and is settled.
        factory.Ledger.AllowSettlements(pumpId);
        factory.Clock.Advance(TimeSpan.FromDays(1));
        using (var next = await ops.PostAsync(new Uri("/api/merchants/ops/settlements/run?date=" + Today(), UriKind.Relative), null, Cancel))
        {
            next.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var settled = await WaitForSettlementAsync(pump, "Settled");
        settled.GetProperty("paymentCount").GetInt32().ShouldBe(1);
        settled.GetProperty("netPaisa").GetInt64().ShouldBe(500_000 - 1_500 - 240);
        (await GetAsync(pump, "/api/merchants/me/settlements")).GetArrayLength().ShouldBe(2);

        bool SettledFor(Guid merchantId, (string EventType, JsonElement Payload) e) => e.EventType == "topup.merchant_settled" && e.Payload.GetProperty("merchantId").GetGuid() == merchantId;
        var events = factory.ReadUntil(KafkaTopics.TopupEvents, read => read.Count(e => SettledFor(pumpId, e)) >= 2 && read.Any(e => SettledFor(shopId, e)));
        events.Where(e => SettledFor(pumpId, e)).Select(e => e.Payload.GetProperty("status").GetString()).ShouldBe(["Failed", "Settled"]);
        events.Single(e => SettledFor(shopId, e)).Payload.GetProperty("netPaisa").GetInt64().ShouldBe(148_608);
    }

    [Fact]
    public async Task Customers_cannot_run_settlement()
    {
        using var customer = factory.ClientFor(Guid.NewGuid());

        using var response = await customer.PostAsync(new Uri("/api/merchants/ops/settlements/run", UriKind.Relative), null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private string Today() => DateOnly.FromDateTime(factory.Clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5)).DateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<JsonElement> WaitForSettlementAsync(HttpClient merchant, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement settlements;
        do
        {
            settlements = await GetAsync(merchant, "/api/merchants/me/settlements");
            if (settlements.GetArrayLength() > 0 && settlements[0].GetProperty("status").GetString() == status)
            {
                return settlements[0];
            }

            await Task.Delay(100, Cancel);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"No {status} settlement: {settlements}");
    }
}
