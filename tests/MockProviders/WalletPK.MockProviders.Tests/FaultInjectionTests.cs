using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using WalletPK.MockProviders.Tests.Infrastructure;
using static WalletPK.MockProviders.Tests.Infrastructure.TestKit;

namespace WalletPK.MockProviders.Tests;

/// <summary>M5.1 and M5.7: every provider can be slowed, broken, timed out or taken down at runtime.</summary>
[Collection(MockProvidersCollection.Name)]
public sealed class FaultInjectionTests(MockProvidersFactory factory) : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await factory.ClearFaultsAsync();

    private static object Payment(string paymentId, string consumer, long amount) => new { paymentId, consumerNumber = consumer, amountPaisa = amount };

    private static async Task<long> PayableAsync(HttpClient client, string consumer) =>
        (await client.GetJsonAsync($"/billers/KE/bills/{consumer}")).GetProperty("payablePaisa").GetInt64();

    [Fact]
    public async Task Latency_slows_every_answer_down()
    {
        using var client = factory.Client();
        await factory.SetFaultsAsync("biller:KE", new { latencyMs = 400 });

        var stopwatch = Stopwatch.StartNew();
        await client.GetJsonAsync($"/billers/KE/bills/{TestAccounts.Consumer()}");

        stopwatch.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(380);
    }

    [Fact]
    public async Task An_error_returns_500_and_nothing_happens()
    {
        using var client = factory.Client();
        var consumer = TestAccounts.Consumer();
        var amount = await PayableAsync(client, consumer);
        await factory.SetFaultsAsync("biller:KE", new { errorRate = 1 });
        var paymentId = NewId();

        using var response = await client.PostJsonAsync("/billers/KE/payments", Payment(paymentId, consumer, amount));

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.JsonAsync()).GetProperty("code").GetString().ShouldBe("provider.error");
        await factory.ClearFaultsAsync();
        await client.GetJsonAsync($"/billers/KE/payments/{paymentId}", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_timeout_hangs_then_returns_504_and_nothing_happens()
    {
        using var client = factory.Client();
        var consumer = TestAccounts.Consumer();
        var amount = await PayableAsync(client, consumer);
        await factory.SetFaultsAsync("biller:KE", new { timeoutRate = 1, timeoutMs = 300 });
        var paymentId = NewId();

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.PostJsonAsync("/billers/KE/payments", Payment(paymentId, consumer, amount));

        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        stopwatch.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(280);
        await factory.ClearFaultsAsync();
        await client.GetJsonAsync($"/billers/KE/payments/{paymentId}", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Accept_but_timeout_does_the_work_and_never_answers_so_only_a_status_query_tells()
    {
        using var client = factory.Client();
        var consumer = TestAccounts.Consumer();
        var amount = await PayableAsync(client, consumer);
        await factory.SetFaultsAsync("biller:KE", new { acceptButTimeoutRate = 1, timeoutMs = 300 });
        var paymentId = NewId();

        // The caller sees the connection die: did the bill get paid?
        await Should.ThrowAsync<Exception>(async () =>
        {
            using var response = await client.PostJsonAsync("/billers/KE/payments", Payment(paymentId, consumer, amount));
        });

        await factory.ClearFaultsAsync();
        var payment = await client.GetJsonAsync($"/billers/KE/payments/{paymentId}");
        payment.GetProperty("status").GetString().ShouldBe("Completed");
        payment.GetProperty("amountPaisa").GetInt64().ShouldBe(amount);
        (await client.GetJsonAsync($"/billers/KE/bills/{consumer}")).GetProperty("status").GetString().ShouldBe("Paid");

        // Retrying with the same ID is safe: the same payment comes back, nothing is paid twice.
        using var retry = await client.PostJsonAsync("/billers/KE/payments", Payment(paymentId, consumer, amount));
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retry.JsonAsync()).GetProperty("billerReference").GetString().ShouldBe(payment.GetProperty("billerReference").GetString());
    }

    [Fact]
    public async Task An_outage_answers_503_or_drops_the_connection()
    {
        using var client = factory.Client();
        await factory.SetFaultsAsync("telco:JAZZ", new { outage = true });

        using (var unavailable = await client.PostJsonAsync("/telcos/JAZZ/topups", new { topUpId = NewId(), msisdn = TestAccounts.JazzNumber(), amountPaisa = 10_000 }))
        {
            unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await unavailable.JsonAsync()).GetProperty("code").GetString().ShouldBe("provider.unavailable");
        }

        await factory.SetFaultsAsync("telco:JAZZ", new { outage = true, outageMode = "ConnectionReset" });
        await Should.ThrowAsync<Exception>(async () =>
        {
            using var response = await client.PostJsonAsync("/telcos/JAZZ/topups", new { topUpId = NewId(), msisdn = TestAccounts.JazzNumber(), amountPaisa = 10_000 });
        });

        // Other networks are unaffected.
        using var zong = await client.PostJsonAsync("/telcos/ZONG/topups", new { topUpId = NewId(), msisdn = "03111234567", amountPaisa = 10_000 });
        zong.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Faults_can_end_by_themselves()
    {
        using var client = factory.Client();
        await factory.SetFaultsAsync("nadra", new { outage = true, durationSeconds = 1 });

        using (var during = await client.PostJsonAsync("/nadra/v1/verifications", new { cnic = "35202-1234567-1", fullName = "Ali Khan", dateOfBirth = "1990-01-01" }))
        {
            during.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }

        await Task.Delay(1_100, Cancel);
        using var after = await client.PostJsonAsync("/nadra/v1/verifications", new { cnic = "35202-1234567-1", fullName = "Ali Khan", dateOfBirth = "1990-01-01" });
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_control_api_lists_providers_validates_settings_and_resets()
    {
        using var admin = factory.Admin();
        await factory.SetFaultsAsync("bank:HBL", new { latencyMs = 100, errorRate = 0.25 });

        var listed = (await admin.GetJsonAsync("/admin/faults")).EnumerateArray().ToList();
        listed.Select(item => item.GetProperty("provider").GetString()).ShouldBe(
            ["raast", "bank:HBL", "bank:MEEZAN", "bank:UBL", "bank:MCB", "biller:KE", "biller:LESCO", "biller:SNGPL", "biller:PTCL",
             "telco:JAZZ", "telco:ZONG", "telco:TELENOR", "telco:UFONE", "nadra"]);
        listed.Single(item => item.GetProperty("provider").GetString() == "bank:HBL")
            .GetProperty("faults").GetProperty("settings").GetProperty("errorRate").GetDouble().ShouldBe(0.25);

        using (var unknown = await admin.PutAsJsonAsync(new Uri("/admin/faults/bank:XYZ", UriKind.Relative), new { latencyMs = 1 }, Cancel))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var invalid = await admin.PutAsJsonAsync(new Uri("/admin/faults/bank:HBL", UriKind.Relative), new { errorRate = 0.7, timeoutRate = 0.5 }, Cancel))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await invalid.JsonAsync()).GetProperty("code").GetString().ShouldBe("faults.invalid");
        }

        await factory.ClearFaultsAsync();
        (await admin.GetJsonAsync("/admin/faults")).EnumerateArray()
            .All(item => item.GetProperty("faults").ValueKind == System.Text.Json.JsonValueKind.Null).ShouldBeTrue();
    }

    [Fact]
    public async Task Keys_are_required_and_kept_apart()
    {
        using var anonymous = factory.CreateClient();
        using var client = factory.Client();

        (await anonymous.GetAsync(new Uri("/billers", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(new Uri("/admin/faults", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync(new Uri("/admin/faults", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync(new Uri("/billers", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
