using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Contracts.Fraud;
using WalletPK.Notifications.Api.Realtime;
using WalletPK.Notifications.Application;
using WalletPK.Notifications.IntegrationTests.Infrastructure;
using static WalletPK.Notifications.IntegrationTests.Infrastructure.NotificationSteps;

namespace WalletPK.Notifications.IntegrationTests;

/// <summary>M11.1: the wallet and Ops hubs, who may connect, and the Redis backplane across instances.</summary>
[Collection(NotificationsCollection.Name)]
public class RealtimeTests(NotificationsFactory factory)
{
    [Fact]
    public async Task The_hubs_need_a_token_and_the_Ops_hub_needs_staff()
    {
        using var client = factory.CreateClient();
        var customer = factory.CreateToken(Guid.NewGuid());
        var ops = factory.CreateToken(Guid.NewGuid(), role: "ops");

        async Task<HttpStatusCode> NegotiateAsync(string path, string? token)
        {
            var query = token is null ? "" : $"&access_token={token}";
            using var response = await client.PostAsync(new Uri($"{path}/negotiate?negotiateVersion=1{query}", UriKind.Relative), null, Cancel);
            return response.StatusCode;
        }

        (await NegotiateAsync(WalletHub.Path, null)).ShouldBe(HttpStatusCode.Unauthorized);
        (await NegotiateAsync(WalletHub.Path, customer)).ShouldBe(HttpStatusCode.OK);
        (await NegotiateAsync(OpsHub.Path, customer)).ShouldBe(HttpStatusCode.Forbidden);
        (await NegotiateAsync(OpsHub.Path, ops)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Staff_see_fraud_alerts_and_freezes_live()
    {
        await using var opsTab = await factory.OpenAsync(OpsHub.Path, Guid.NewGuid(), role: "compliance");
        var user = Guid.NewGuid();

        await factory.PublishAsync(new FraudAlertRaised(
            Guid.NewGuid(), user, Guid.NewGuid(), "Payment", "High", 100, "Block", ["sim-swap-drain"], "P2PTransfer of Rs. 40,000 to +92300*****12: blocked", Guid.NewGuid(), DateTimeOffset.UtcNow));
        await factory.PublishAsync(new AccountFreezeChanged(user, true, "Blocked by rules sim-swap-drain (case 12)", "rules", DateTimeOffset.UtcNow));

        var alert = await opsTab.NextAsync("Event", item => item.GetProperty("userId").GetGuid() == user && item.GetProperty("kind").GetString() == "fraud.alert");
        alert.GetProperty("severity").GetString().ShouldBe("High");
        alert.GetProperty("detail").GetString()!.ShouldContain("sim-swap-drain");
        var freeze = await opsTab.NextAsync("Event", item => item.GetProperty("userId").GetGuid() == user && item.GetProperty("kind").GetString() == "account.frozen");
        freeze.GetProperty("detail").GetString().ShouldBe("Blocked by rules sim-swap-drain (case 12)");
    }

    [Fact]
    public async Task A_push_from_one_instance_reaches_a_browser_connected_to_another_through_the_Redis_backplane()
    {
        await using var second = factory.WithWebHostBuilder(_ => { });
        var user = Guid.NewGuid();
        var update = new PaymentUpdate("bank", Guid.NewGuid(), "Completed", "Done");

        // A browser on the second instance, the push from the first.
        await using var tab = await factory.OpenAsync(WalletHub.Path, user, host: second);
        var first = factory.Services.GetRequiredService<IHubContext<WalletHub, IWalletClient>>();
        var pushed = await UntilReceivedAsync(tab, () => first.Clients.User(user.ToString()).PaymentChanged(update));
        pushed.GetProperty("id").GetGuid().ShouldBe(update.Id);

        // And the other way round.
        await using var firstTab = await factory.OpenAsync(WalletHub.Path, user);
        var other = second.Services.GetRequiredService<IHubContext<WalletHub, IWalletClient>>();
        var failed = await UntilReceivedAsync(firstTab, () => other.Clients.User(user.ToString()).PaymentChanged(update with { Status = "Failed" }));
        failed.GetProperty("status").GetString().ShouldBe("Failed");
    }

    /// <summary>
    /// SignalR answers the handshake before the instance has subscribed the user's Redis channel, so the very
    /// first push can come too early (the app reloads over HTTP on connect anyway): push until it arrives.
    /// </summary>
    private static async Task<System.Text.Json.JsonElement> UntilReceivedAsync(HubTab tab, Func<Task> push)
    {
        for (var attempt = 0; ; attempt++)
        {
            await push();
            try
            {
                return await tab.NextAsync("PaymentChanged", seconds: 1);
            }
            catch (OperationCanceledException) when (attempt < 10 && !Cancel.IsCancellationRequested)
            {
                // Not subscribed yet.
            }
        }
    }
}
