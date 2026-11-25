using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Common.Time;
using WalletPK.Contracts.TopupMerchant;

namespace WalletPK.Reconciliation.IntegrationTests.Infrastructure;

public static class ReconSteps
{
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Today in Pakistan: the mock files list what was settled today.</summary>
    public static DateOnly Today => TimeProvider.System.GetBusinessDate();

    /// <summary>The top-up service's event for a top-up (Completed unless said otherwise).</summary>
    public static TopUpChanged Changed(TelcoTopUp topUp, string network, string status = "Completed", long? amount = null, string? reference = null) =>
        new(topUp.TopUpId, Guid.NewGuid(), network, topUp.Msisdn, null, amount ?? topUp.AmountPaisa, status, status, $"Top-up {status}", DateTimeOffset.UtcNow,
            status == "Completed" ? reference ?? topUp.TelcoReference : reference);

    /// <summary>Publishes the events and waits until reconciliation has every payment in the state they say.</summary>
    public static async Task TrackAsync(this ReconciliationFactory factory, params TopUpChanged[] events)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(events);
        foreach (var change in events)
        {
            await factory.PublishAsync(change);
        }

        using var client = factory.ClientFor("ops");
        foreach (var change in events)
        {
            await WaitAsync(
                async () =>
                {
                    using var response = await client.GetAsync(new Uri($"/api/reconciliation/payments/{change.TopUpId}", UriKind.Relative), Cancel);
                    return response.StatusCode == HttpStatusCode.OK ? await response.JsonAsync() : (JsonElement?)null;
                },
                payment => payment?.GetProperty("serviceStatus").GetString() == change.Status,
                $"top-up {change.TopUpId} as {change.Status}");
        }
    }

    public static async Task<HttpResponseMessage> StartAsync(this HttpClient client, DateOnly date, params string[] parties) =>
        await client.PostAsJsonAsync(new Uri("/api/reconciliation/runs", UriKind.Relative), new { date, parties }, Cancel);

    /// <summary>Runs one party's day and returns the run with its breaks.</summary>
    public static async Task<JsonElement> ReconcileAsync(this ReconciliationFactory factory, string party, DateOnly? date = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        using var finance = factory.ClientFor("finance");
        using var started = await finance.StartAsync(date ?? Today, party);
        started.StatusCode.ShouldBe(HttpStatusCode.OK, await started.Content.ReadAsStringAsync(Cancel));
        var run = (await started.JsonAsync()).EnumerateArray().Single();
        return await finance.GetFromJsonAsync<JsonElement>(new Uri($"/api/reconciliation/runs/{run.GetProperty("id").GetString()}", UriKind.Relative), Cancel);
    }

    public static IEnumerable<JsonElement> Breaks(this JsonElement detail, string kind) =>
        detail.GetProperty("breaks").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == kind);

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);

    public static async Task<T> WaitAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, int seconds = 30)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(done);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {seconds} s for {what}.");
            }

            await Task.Delay(100, Cancel);
        }
    }
}
