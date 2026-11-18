using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using WalletPK.TopupMerchant.IntegrationTests.Infrastructure;

namespace WalletPK.TopupMerchant.IntegrationTests;

/// <summary>M8.1: mobile top-ups end to end, including refusals, outages, unknown outcomes and the dead-letter queue.</summary>
[Collection(TopupMerchantCollection.Name)]
public class TopUpTests(TopupMerchantFactory factory) : IAsyncLifetime
{
    private bool _faulted;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        factory.Clock.Reset();
        using var admin = factory.Providers.Admin();
        using var _ = await admin.DeleteAsync(new Uri("/admin/faults", UriKind.Relative), CancellationToken.None);

        // A network a test broke may have its circuit open: let it close before the next test.
        if (_faulted)
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5), CancellationToken.None);
        }
    }

    /// <summary>A random number on the network with <paramref name="prefix"/> ("300" Jazz, "310" Zong, "330" Ufone, "340" Telenor), not ending 0000.</summary>
    private static string Number(string prefix) =>
        $"0{prefix}{Random.Shared.Next(100, 1000).ToString(CultureInfo.InvariantCulture)}{Random.Shared.Next(1001, 10000).ToString(CultureInfo.InvariantCulture)}";

    private static string E164(string local) => $"+92{local[1..]}";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    private static async Task<HttpResponseMessage> TopUpAsync(HttpClient client, string msisdn, long amount, string? bundleCode = null, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/topups")
        {
            Content = JsonContent.Create(new { msisdn, amountPaisa = amount, bundleCode, bundleName = bundleCode is null ? null : "Test bundle" }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request, Cancel);
    }

    private static async Task<Guid> StartAsync(HttpClient client, string msisdn, long amount, string? bundleCode = null)
    {
        using var response = await TopUpAsync(client, msisdn, amount, bundleCode);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Cancel));
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> WaitForStepAsync(HttpClient client, Guid id, string step, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonElement topUp;
        do
        {
            using var response = await client.GetAsync(new Uri($"/api/topups/{id}", UriKind.Relative), Cancel);
            topUp = await JsonAsync(response);
            if (topUp.GetProperty("step").GetString() == step)
            {
                return topUp;
            }

            await Task.Delay(100, Cancel);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Top-up {id} did not reach {step}: {topUp}");
    }

    private static string[] Steps(JsonElement topUp) =>
        [.. topUp.GetProperty("timeline").EnumerateArray().Select(step => step.GetProperty("state").GetString()!)];

    private async Task SetFaultAsync(string provider, object settings)
    {
        _faulted = true;
        using var admin = factory.Providers.Admin();
        using var response = await admin.PutAsJsonAsync(new Uri($"/admin/faults/{provider}", UriKind.Relative), settings, Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
    }

    private async Task ClearFaultAsync(string provider)
    {
        using var admin = factory.Providers.Admin();
        using var _ = await admin.DeleteAsync(new Uri($"/admin/faults/{provider}", UriKind.Relative), Cancel);
    }

    /// <summary>Whether the network holds top-up <paramref name="id"/> (its status query).</summary>
    private async Task<bool> NetworkHasAsync(string network, Guid id)
    {
        using var client = factory.Providers.Client();
        using var response = await client.GetAsync(new Uri($"/telcos/{network}/topups/{id}", UriKind.Relative), Cancel);
        return response.StatusCode == HttpStatusCode.OK;
    }

    [Fact]
    public async Task Networks_detection_and_bundles_are_listed()
    {
        using var client = factory.ClientFor(Guid.NewGuid());

        var networks = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/topups/networks", UriKind.Relative), Cancel);
        networks.EnumerateArray().Select(network => network.GetProperty("code").GetString()).ShouldBe(["JAZZ", "ZONG", "UFONE", "TELENOR"], ignoreOrder: true);

        var detected = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/topups/detect?msisdn=0345%201234567", UriKind.Relative), Cancel);
        detected.GetProperty("code").GetString().ShouldBe("TELENOR");

        using (var unknown = await client.GetAsync(new Uri("/api/topups/detect?msisdn=03551234567", UriKind.Relative), Cancel))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await JsonAsync(unknown)).GetProperty("code").GetString().ShouldBe("topup.unknown_network");
        }

        var bundles = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/topups/networks/JAZZ/bundles", UriKind.Relative), Cancel);
        bundles.GetArrayLength().ShouldBeGreaterThan(0);
        bundles[0].GetProperty("code").GetString()!.ShouldStartWith("JAZZ-");
    }

    [Fact]
    public async Task Airtime_reserves_tops_up_and_settles_to_the_telco_settlement_account()
    {
        var user = Guid.NewGuid();
        using var client = factory.ClientFor(user);
        var number = Number("300");

        var id = await StartAsync(client, number, 50_000);
        var topUp = await WaitForStepAsync(client, id, "Completed");

        topUp.GetProperty("status").GetString().ShouldBe("Completed");
        topUp.GetProperty("network").GetString().ShouldBe("JAZZ");
        topUp.GetProperty("msisdn").GetString().ShouldBe(E164(number));
        topUp.GetProperty("telcoReference").GetString()!.ShouldStartWith("JAZZ");
        Steps(topUp).ShouldBe(["Reserving", "ToppingUp", "Settling", "Completed"]);

        var commands = factory.Ledger.CommandsFor(id);
        var hold = commands.OfType<PlaceHold>().ShouldHaveSingleItem();
        hold.UserId.ShouldBe(user);
        hold.AmountPaisa.ShouldBe(50_000);
        hold.Purpose.ShouldBe("MobileTopup");
        var capture = commands.OfType<CaptureHold>().ShouldHaveSingleItem();
        capture.Credits.ShouldHaveSingleItem().ShouldBe(new HoldCredit("TELCO_SETTLEMENT", 50_000));
        (await NetworkHasAsync("JAZZ", id)).ShouldBeTrue();

        var events = factory.ReadUntil(KafkaTopics.TopupEvents, read => read.Any(e => e.EventType == "topup.topup_changed" && e.Payload.GetProperty("topUpId").GetGuid() == id));
        events.Single(e => e.EventType == "topup.topup_changed" && e.Payload.GetProperty("topUpId").GetGuid() == id).Payload.GetProperty("status").GetString().ShouldBe("Completed");

        var mine = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/topups", UriKind.Relative), Cancel);
        mine.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldContain(id);
    }

    [Fact]
    public async Task A_bundle_is_bought_at_its_price()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        var bundles = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/topups/networks/UFONE/bundles", UriKind.Relative), Cancel);
        var bundle = bundles[0];

        var id = await StartAsync(client, Number("333"), bundle.GetProperty("pricePaisa").GetInt64(), bundle.GetProperty("code").GetString());
        var topUp = await WaitForStepAsync(client, id, "Completed");

        topUp.GetProperty("bundleCode").GetString().ShouldBe(bundle.GetProperty("code").GetString());
        topUp.GetProperty("amountPaisa").GetInt64().ShouldBe(bundle.GetProperty("pricePaisa").GetInt64());
    }

    [Fact]
    public async Task A_bundle_at_the_wrong_price_is_refused_by_the_network_and_the_money_returned()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        var bundles = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/topups/networks/UFONE/bundles", UriKind.Relative), Cancel);

        var id = await StartAsync(client, Number("334"), bundles[0].GetProperty("pricePaisa").GetInt64() - 100, bundles[0].GetProperty("code").GetString());
        var topUp = await WaitForStepAsync(client, id, "Failed");

        topUp.GetProperty("failureCode").GetString().ShouldBe("telco.amount_mismatch");
        Steps(topUp).ShouldBe(["Reserving", "ToppingUp", "Returning", "Failed"]);
        factory.Ledger.HoldState(id).ShouldBe("Released");
    }

    [Fact]
    public async Task An_inactive_number_is_refused_and_the_money_returned()
    {
        using var client = factory.ClientFor(Guid.NewGuid());

        var id = await StartAsync(client, "03211230000", 10_000);
        var topUp = await WaitForStepAsync(client, id, "Failed");

        topUp.GetProperty("status").GetString().ShouldBe("Failed");
        topUp.GetProperty("failureCode").GetString().ShouldBe("telco.msisdn_inactive");
        factory.Ledger.HoldState(id).ShouldBe("Released");
        factory.Ledger.CommandsFor(id).OfType<CaptureHold>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_hold_fails_without_calling_the_network()
    {
        var user = Guid.NewGuid();
        factory.Ledger.Refuse(user, "wallet.insufficient_balance", "Not enough money in your wallet.");
        using var client = factory.ClientFor(user);

        var id = await StartAsync(client, Number("301"), 10_000);
        var topUp = await WaitForStepAsync(client, id, "Failed");

        topUp.GetProperty("failureCode").GetString().ShouldBe("wallet.insufficient_balance");
        Steps(topUp).ShouldBe(["Reserving", "Failed"]);
        (await NetworkHasAsync("JAZZ", id)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_same_idempotency_key_returns_the_same_top_up_and_cannot_be_reused_for_another()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        var number = Number("302");
        var key = Guid.NewGuid().ToString();

        using var first = await TopUpAsync(client, number, 20_000, key: key);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id = (await JsonAsync(first)).GetProperty("id").GetGuid();
        await WaitForStepAsync(client, id, "Completed");

        using var again = await TopUpAsync(client, number, 20_000, key: key);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(again)).GetProperty("id").GetGuid().ShouldBe(id);

        using var other = await TopUpAsync(client, number, 30_000, key: key);
        other.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        factory.Ledger.CommandsFor(id).OfType<PlaceHold>().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("12345", 10_000, "topup.invalid_number")]
    [InlineData("03551234567", 10_000, "topup.unknown_network")]
    [InlineData("03001234567", 1_500, "topup.invalid_amount")]
    [InlineData("03001234567", 600_000, "topup.invalid_amount")]
    [InlineData("03001234567", 10_050, "topup.invalid_amount")]
    public async Task Invalid_top_ups_are_refused(string msisdn, long amount, string code)
    {
        using var client = factory.ClientFor(Guid.NewGuid());

        using var response = await TopUpAsync(client, msisdn, amount);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task A_network_outage_is_retried_and_the_top_up_goes_through_when_it_is_back()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        await SetFaultAsync("telco:ZONG", new { outage = true });

        var id = await StartAsync(client, Number("312"), 30_000);
        await WaitForStepAsync(client, id, "Retrying");

        await ClearFaultAsync("telco:ZONG");
        var topUp = await WaitForStepAsync(client, id, "Completed");

        Steps(topUp).ShouldContain("Retrying");
        (await NetworkHasAsync("ZONG", id)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_network_down_too_long_gives_up_and_returns_the_money()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        await SetFaultAsync("telco:TELENOR", new { outage = true });

        var id = await StartAsync(client, Number("341"), 30_000);
        await WaitForStepAsync(client, id, "Retrying");
        factory.Clock.Advance(TimeSpan.FromMinutes(11));

        var topUp = await WaitForStepAsync(client, id, "Failed");
        topUp.GetProperty("failureCode").GetString().ShouldBe("topup.network_unavailable");
        factory.Ledger.HoldState(id).ShouldBe("Released");
    }

    [Fact]
    public async Task A_top_up_applied_without_an_answer_is_confirmed_by_the_status_query_and_never_sent_twice()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        await SetFaultAsync("telco:JAZZ", new { acceptButTimeoutRate = 1.0, timeoutMs = 200 });

        var id = await StartAsync(client, Number("322"), 40_000);
        await WaitForStepAsync(client, id, "Verifying");
        await ClearFaultAsync("telco:JAZZ");

        var topUp = await WaitForStepAsync(client, id, "Completed");
        Steps(topUp).ShouldBe(["Reserving", "ToppingUp", "Verifying", "Settling", "Completed"]);
        factory.Ledger.CommandsFor(id).OfType<CaptureHold>().ShouldHaveSingleItem().Credits.Single().AmountPaisa.ShouldBe(40_000);
    }

    [Fact]
    public async Task A_top_up_the_network_never_confirms_is_dead_lettered_with_the_money_still_reserved()
    {
        var user = Guid.NewGuid();
        using var client = factory.ClientFor(user);
        await SetFaultAsync("telco:UFONE", new { timeoutRate = 1.0, timeoutMs = 2000 });

        var id = await StartAsync(client, Number("335"), 25_000);
        await WaitForStepAsync(client, id, "Verifying");
        factory.Clock.Advance(TimeSpan.FromHours(25));

        var topUp = await WaitForStepAsync(client, id, "DeadLettered");
        topUp.GetProperty("status").GetString().ShouldBe("UnderReview");
        factory.Ledger.HoldState(id).ShouldBe("Active");

        var dlq = factory.ReadUntil(KafkaTopics.TopupDeadLetter, read => read.Any(e => e.Payload.GetProperty("topUpId").GetGuid() == id));
        var message = dlq.Single(e => e.Payload.GetProperty("topUpId").GetGuid() == id);
        message.EventType.ShouldBe("topup.topup_dead_lettered");
        message.Payload.GetProperty("userId").GetGuid().ShouldBe(user);
        message.Payload.GetProperty("lastState").GetString().ShouldBe("Verifying");
    }

    [Fact]
    public async Task Another_customers_top_up_is_not_found()
    {
        using var owner = factory.ClientFor(Guid.NewGuid());
        using var stranger = factory.ClientFor(Guid.NewGuid());
        var id = await StartAsync(owner, Number("303"), 10_000);

        using var response = await stranger.GetAsync(new Uri($"/api/topups/{id}", UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
