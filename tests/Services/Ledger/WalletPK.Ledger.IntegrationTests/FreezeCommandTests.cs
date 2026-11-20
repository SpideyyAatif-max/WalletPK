using System.Net;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Contracts.Ledger;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using WalletPK.Messaging;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>M10.6: the Fraud service freezes and unfreezes all of a user's wallets over ledger.commands.</summary>
[Collection(LedgerCollection.Name)]
public class FreezeCommandTests(LedgerApiFactory factory)
{
    private IntegrationEventRegistry Registry => factory.Services.GetRequiredService<IntegrationEventRegistry>();

    private async Task<ReadMessage> SendAsync(IntegrationEvent command, Guid userId, int nth)
    {
        await factory.PublishAsync(command, Registry, correlationId: $"freeze-{userId}");
        bool Mine(ReadMessage message) => message.Key == userId.ToString() && message.EventType == "ledger.wallets_freeze_changed";
        return factory.ReadUntil(KafkaTopics.LedgerReplies, read => read.Count(Mine) >= nth, TimeSpan.FromSeconds(30)).Where(Mine).ElementAt(nth - 1);
    }

    [Fact]
    public async Task A_frozen_wallet_can_receive_but_not_send_until_it_is_unfrozen()
    {
        var suspect = await factory.CustomerAsync(rupees: 1_000);
        var friend = await factory.CustomerAsync(rupees: 1_000);

        var frozen = await SendAsync(new FreezeWallets(Guid.NewGuid(), suspect.Id.Value, "Rule sim-swap-drain"), suspect.Id.Value, nth: 1);
        frozen.Payload.GetProperty("frozen").GetBoolean().ShouldBeTrue();
        frozen.Payload.GetProperty("walletCount").GetInt32().ShouldBe(1);

        using (var send = await suspect.SendAsync(friend.Phone, 10_000, NewKey()))
        {
            send.StatusCode.ShouldNotBe(HttpStatusCode.OK);
            send.StatusCode.ShouldNotBe(HttpStatusCode.Created);
        }

        using (var receive = await friend.SendAsync(suspect.Phone, 10_000, NewKey()))
        {
            receive.IsSuccessStatusCode.ShouldBeTrue(await receive.Content.ReadAsStringAsync(Cancel));
        }

        (await SendAsync(new UnfreezeWallets(Guid.NewGuid(), suspect.Id.Value, "Case closed: false positive"), suspect.Id.Value, nth: 2))
            .Payload.GetProperty("frozen").GetBoolean().ShouldBeFalse();
        using (var sendAgain = await suspect.SendAsync(friend.Phone, 10_000, NewKey()))
        {
            sendAgain.IsSuccessStatusCode.ShouldBeTrue(await sendAgain.Content.ReadAsStringAsync(Cancel));
        }

        await factory.ShouldBeHealthyAsync();
    }
}
