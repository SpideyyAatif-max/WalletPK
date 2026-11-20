using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WalletPK.Agents.IntegrationTests.Infrastructure;

/// <summary>A signed-in user in the tests, with a wallet in the fake Ledger.</summary>
public sealed record TestUser(Guid Id, string Phone, HttpClient Client) : IDisposable
{
    public void Dispose() => Client.Dispose();
}

/// <summary>An active agent: its owner, and its ID and code.</summary>
public sealed record TestAgent(TestUser Owner, Guid Id, string Code) : IDisposable
{
    public HttpClient Client => Owner.Client;

    public void Dispose() => Owner.Dispose();
}

/// <summary>Shared steps: users, applying and approving agents, transactions, waiting for them.</summary>
public static class AgentSteps
{
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    public static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("code").GetString()!;

    public static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        return await JsonAsync(response);
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Cancel);
    }

    /// <summary>A customer with <paramref name="paisa"/> in their wallet.</summary>
    public static TestUser Customer(this AgentsFactory factory, long paisa = 0, string kycTier = "L1")
    {
        var id = Guid.NewGuid();
        var phone = AgentsFactory.NewPhone();
        factory.Ledger.AddCustomer(id, phone, paisa);
        return new TestUser(id, phone, factory.ClientFor(id, phone, kycTier: kycTier));
    }

    /// <summary>An operator (finance role).</summary>
    public static HttpClient Operator(this AgentsFactory factory) => factory.ClientFor(Guid.NewGuid(), AgentsFactory.NewPhone(), role: "finance");

    /// <summary>Applies, is approved by an operator, waits until the Ledger has opened the float, and loads <paramref name="floatPaisa"/> into it.</summary>
    public static async Task<TestAgent> ActiveAgentAsync(this AgentsFactory factory, long floatPaisa = 0, string shop = "Bismillah Easy Cash", string city = "Multan")
    {
        var owner = factory.Customer(paisa: floatPaisa);
        using var applied = await PostAsync(owner.Client, "/api/agents", new { shopName = shop, city, area = "Hussain Agahi" });
        applied.StatusCode.ShouldBe(HttpStatusCode.Created, await applied.Content.ReadAsStringAsync(Cancel));
        var agent = await JsonAsync(applied);
        var id = agent.GetProperty("id").GetGuid();

        using (var ops = factory.Operator())
        using (var approved = await PostAsync(ops, $"/api/agents/ops/{id}/approve", null))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Cancel));
        }

        await WaitAsync(() => GetAsync(owner.Client, "/api/agents/me"), me => me.GetProperty("status").GetString() == "Active", "agent active");
        var result = new TestAgent(owner, id, agent.GetProperty("code").GetString()!);
        if (floatPaisa > 0)
        {
            var load = await StartAsync(owner.Client, "/api/agents/me/float", new { direction = "ToFloat", amountPaisa = floatPaisa });
            (await WaitForTransactionAsync(owner.Client, load)).GetProperty("status").GetString().ShouldBe("Completed");
        }

        return result;
    }

    /// <summary>Starts a transaction (202) and returns its ID.</summary>
    public static async Task<Guid> StartAsync(HttpClient client, string path, object body, string? key = null)
    {
        using var response = await PostAsync(client, path, body, key ?? Guid.NewGuid().ToString());
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Cancel));
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Waits until the transaction is no longer processing.</summary>
    public static Task<JsonElement> WaitForTransactionAsync(HttpClient client, Guid id) =>
        WaitAsync(() => GetAsync(client, $"/api/agents/transactions/{id}"), transaction => transaction.GetProperty("status").GetString() != "Processing", $"transaction {id}");

    public static async Task<JsonElement> WaitAsync(Func<Task<JsonElement>> read, Func<JsonElement, bool> done, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonElement value;
        do
        {
            value = await read();
            if (done(value))
            {
                return value;
            }

            await Task.Delay(100, Cancel);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Timed out waiting for {what}: {value}");
    }
}
