using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration.Json;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>The Development-only test cash-in the web app uses to fund wallets (M4.7).</summary>
[Collection(LedgerCollection.Name)]
public class DevToolsTests(LedgerApiFactory factory)
{
    private static async Task<HttpResponseMessage> TopUpAsync(HttpClient client, long? paisa, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/wallets/me/dev/top-up", UriKind.Relative))
        {
            Content = JsonContent.Create(new { amountPaisa = paisa }),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Cancel);
    }

    [Fact]
    public async Task A_test_top_up_is_a_cash_in_from_the_bank_float_and_counts_once_per_key()
    {
        var customer = await factory.CustomerAsync();
        var key = NewKey();

        using (var first = await TopUpAsync(customer.Client, 500_000, key))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
            Paisa(await JsonAsync(first), "balance").ShouldBe(500_000);
        }

        using (var again = await TopUpAsync(customer.Client, 500_000, key))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await factory.BalanceAsync(customer.WalletId)).ShouldBe(500_000);
        await customer.WaitForHistoryAsync(entries: 1);
        (await customer.HistoryAsync()).GetProperty("items")[0].GetProperty("title").GetString().ShouldBe("Cash-in");
        await factory.ShouldBeHealthyAsync();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(10_000_001L)]
    public async Task Top_ups_must_be_between_one_paisa_and_rs_100000(long paisa)
    {
        var customer = await factory.CustomerAsync();

        using var response = await TopUpAsync(customer.Client, paisa, NewKey());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("code").GetString().ShouldBe("wallet.dev_top_up_invalid");
    }

    [Fact]
    public async Task A_top_up_needs_an_idempotency_key()
    {
        var customer = await factory.CustomerAsync();

        using var response = await TopUpAsync(customer.Client, 100, key: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Outside_development_there_is_no_top_up()
    {
        await using var staging = factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Staging")
            // Same settings as the tests normally run with; only the environment name differs.
            .ConfigureAppConfiguration(configuration =>
            {
                var appsettings = configuration.Sources.ToList().FindIndex(source => source is JsonConfigurationSource { Path: "appsettings.json" });
                configuration.Sources.Insert(appsettings + 1, new JsonConfigurationSource { Path = "appsettings.Development.json", Optional = false });
            }));
        using var client = staging.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken(WalletPK.Common.Identifiers.UserId.New(), NewPhone()));

        using var response = await TopUpAsync(client, 100, NewKey());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
