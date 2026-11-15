using System.Net;
using WalletPK.MockProviders.Tests.Infrastructure;
using static WalletPK.MockProviders.Tests.Infrastructure.TestKit;

namespace WalletPK.MockProviders.Tests;

/// <summary>M5.3 billers and M5.4 mobile networks.</summary>
[Collection(MockProvidersCollection.Name)]
public sealed class BillerAndTelcoTests(MockProvidersFactory factory)
{
    [Fact]
    public async Task A_bill_is_the_same_every_time_and_can_be_paid_once()
    {
        using var client = factory.Client();
        var consumer = TestAccounts.Consumer(14);
        var bill = await client.GetJsonAsync($"/billers/LESCO/bills/{consumer}");
        (await client.GetJsonAsync($"/billers/LESCO/bills/{consumer}")).ToString().ShouldBe(bill.ToString());

        bill.GetProperty("status").GetString().ShouldBe("Unpaid");
        var within = bill.GetProperty("amountWithinDuePaisa").GetInt64();
        bill.GetProperty("amountAfterDuePaisa").GetInt64().ShouldBe((long)Math.Round(within / 100 * 1.1m, MidpointRounding.AwayFromZero) * 100);
        bill.GetProperty("dueDate").GetString()!.ShouldEndWith("-20");
        var payable = bill.GetProperty("payablePaisa").GetInt64();

        using (var wrongAmount = await client.PostJsonAsync("/billers/LESCO/payments", new { paymentId = NewId(), consumerNumber = consumer, amountPaisa = payable - 100 }))
        {
            wrongAmount.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await wrongAmount.JsonAsync()).GetProperty("code").GetString().ShouldBe("biller.amount_mismatch");
        }

        var paymentId = NewId();
        using (var paid = await client.PostJsonAsync("/billers/LESCO/payments", new { paymentId, consumerNumber = consumer, amountPaisa = payable }))
        {
            paid.StatusCode.ShouldBe(HttpStatusCode.OK);
            var payment = await paid.JsonAsync();
            payment.GetProperty("status").GetString().ShouldBe("Completed");
            payment.GetProperty("billingMonth").GetString().ShouldBe(bill.GetProperty("billingMonth").GetString());
        }

        (await client.GetJsonAsync($"/billers/LESCO/bills/{consumer}")).GetProperty("status").GetString().ShouldBe("Paid");
        using var twice = await client.PostJsonAsync("/billers/LESCO/payments", new { paymentId = NewId(), consumerNumber = consumer, amountPaisa = payable });
        twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await twice.JsonAsync()).GetProperty("code").GetString().ShouldBe("biller.bill_already_paid");
    }

    [Fact]
    public async Task Concurrent_payments_of_one_bill_pay_it_exactly_once()
    {
        using var client = factory.Client();
        var consumer = TestAccounts.Consumer(11);
        var payable = (await client.GetJsonAsync($"/billers/SNGPL/bills/{consumer}")).GetProperty("payablePaisa").GetInt64();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var response = await client.PostJsonAsync("/billers/SNGPL/payments", new { paymentId = NewId(), consumerNumber = consumer, amountPaisa = payable });
            return response.StatusCode;
        }));

        attempts.Count(status => status == HttpStatusCode.OK).ShouldBe(1);
        attempts.Count(status => status == HttpStatusCode.Conflict).ShouldBe(7);
    }

    [Fact]
    public async Task Test_consumer_numbers_cover_the_unhappy_paths()
    {
        using var client = factory.Client();

        (await client.GetJsonAsync($"/billers/KE/bills/{TestAccounts.Consumer(lastFour: "0000")}", HttpStatusCode.NotFound)).GetProperty("code").GetString().ShouldBe("biller.consumer_not_found");
        (await client.GetJsonAsync($"/billers/KE/bills/{TestAccounts.Consumer(lastFour: "1111")}")).GetProperty("status").GetString().ShouldBe("Paid");
        (await client.GetJsonAsync("/billers/KE/bills/12345", HttpStatusCode.BadRequest)).GetProperty("code").GetString().ShouldBe("biller.invalid_consumer_number");
        (await client.GetJsonAsync("/billers/WAPDA/bills/1234567890123", HttpStatusCode.NotFound)).GetProperty("code").GetString().ShouldBe("biller.unknown");

        var disconnected = TestAccounts.Consumer(lastFour: "2222");
        var payable = (await client.GetJsonAsync($"/billers/KE/bills/{disconnected}")).GetProperty("payablePaisa").GetInt64();
        using var refused = await client.PostJsonAsync("/billers/KE/payments", new { paymentId = NewId(), consumerNumber = disconnected, amountPaisa = payable });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.JsonAsync()).GetProperty("code").GetString().ShouldBe("biller.connection_disconnected");
    }

    [Fact]
    public async Task Top_ups_go_to_the_right_network_in_whole_rupees_or_as_bundles()
    {
        using var client = factory.Client();
        var number = TestAccounts.JazzNumber();

        var topUpId = NewId();
        using (var airtime = await client.PostJsonAsync("/telcos/JAZZ/topups", new { topUpId, msisdn = number, amountPaisa = 50_000 }))
        {
            airtime.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await airtime.JsonAsync()).GetProperty("msisdn").GetString().ShouldBe("+92" + number[1..]);
        }

        (await client.GetJsonAsync($"/telcos/JAZZ/topups/{topUpId}")).GetProperty("status").GetString().ShouldBe("Completed");

        var bundles = (await client.GetJsonAsync("/telcos/JAZZ/bundles")).EnumerateArray().ToList();
        var bundle = bundles[0];
        using (var bought = await client.PostJsonAsync("/telcos/JAZZ/topups", new { topUpId = NewId(), msisdn = number, bundleCode = bundle.GetProperty("code").GetString() }))
        {
            (await bought.JsonAsync()).GetProperty("amountPaisa").GetInt64().ShouldBe(bundle.GetProperty("pricePaisa").GetInt64());
        }

        async Task<string?> CodeAsync(string telco, object body)
        {
            using var response = await client.PostJsonAsync($"/telcos/{telco}/topups", body);
            return (await response.JsonAsync()).GetProperty("code").GetString();
        }

        (await CodeAsync("ZONG", new { topUpId = NewId(), msisdn = number, amountPaisa = 10_000 })).ShouldBe("telco.msisdn_not_on_network");
        (await CodeAsync("JAZZ", new { topUpId = NewId(), msisdn = TestAccounts.JazzNumber("0000"), amountPaisa = 10_000 })).ShouldBe("telco.msisdn_inactive");
        (await CodeAsync("JAZZ", new { topUpId = NewId(), msisdn = number, amountPaisa = 1_000 })).ShouldBe("telco.invalid_amount");
        (await CodeAsync("JAZZ", new { topUpId = NewId(), msisdn = number, amountPaisa = 10_050 })).ShouldBe("telco.invalid_amount");
        (await CodeAsync("JAZZ", new { topUpId = NewId(), msisdn = number, bundleCode = "ZONG-DAILY-SUPER" })).ShouldBe("telco.unknown_bundle");
        (await CodeAsync("JAZZ", new { topUpId, msisdn = number, amountPaisa = 60_000 })).ShouldBe("provider.duplicate_id");
    }

    [Fact]
    public async Task Each_network_owns_its_prefixes()
    {
        using var client = factory.Client();
        var telcos = (await client.GetJsonAsync("/telcos")).EnumerateArray().ToList();

        telcos.Select(telco => telco.GetProperty("code").GetString()).ShouldBe(["JAZZ", "ZONG", "TELENOR", "UFONE"]);
        var prefixes = telcos.SelectMany(telco => telco.GetProperty("prefixes").EnumerateArray().Select(prefix => prefix.GetString())).ToList();
        prefixes.Distinct().Count().ShouldBe(prefixes.Count);
    }
}
