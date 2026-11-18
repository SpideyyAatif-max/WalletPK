using Microsoft.Extensions.DependencyInjection;
using WalletPK.Contracts.Ledger;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using WalletPK.Messaging;
using WalletPK.Messaging.Serialization;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>M6.1: Payments moves money between wallets with TransferFunds on ledger.commands; every answer is final.</summary>
[Collection(LedgerCollection.Name)]
public class TransferCommandTests(LedgerApiFactory factory)
{
    private IntegrationEventRegistry Registry => factory.Services.GetRequiredService<IntegrationEventRegistry>();

    /// <summary>Sends a copy of the command (a new event, as a Saga's resend is) and waits for the <paramref name="nth"/> answer.</summary>
    private async Task<ReadMessage> SendAsync(TransferFunds command, int nth = 1)
    {
        var copy = new TransferFunds(command.TransferId, command.ReplyTo, command.SenderUserId, command.ToWalletNumber, command.AmountPaisa, command.DeviceId);
        await factory.PublishAsync(copy, Registry, correlationId: $"p2p-{command.TransferId}");
        bool Mine(ReadMessage message) => message.Key == command.TransferId.ToString();
        return factory.ReadUntil(KafkaTopics.LedgerReplies, read => read.Count(Mine) >= nth, TimeSpan.FromSeconds(30)).Where(Mine).ElementAt(nth - 1);
    }

    [Fact]
    public async Task A_transfer_moves_the_money_with_its_fee_once_however_often_it_is_asked_for()
    {
        var sender = await factory.CustomerAsync(rupees: 1_000);
        var recipient = await factory.CustomerAsync(rupees: 0);
        var command = new TransferFunds(Guid.CreateVersion7(), Guid.CreateVersion7(), sender.Id.Value, recipient.Phone, 50_000, "device-1");

        var done = await SendAsync(command);
        var again = await SendAsync(command, nth: 2);

        done.EventType.ShouldBe("ledger.funds_transferred");
        done.Payload.GetProperty("replyTo").GetGuid().ShouldBe(command.ReplyTo);
        done.Payload.GetProperty("amountPaisa").GetInt64().ShouldBe(50_000);
        var charges = done.Payload.GetProperty("feePaisa").GetInt64() + done.Payload.GetProperty("taxPaisa").GetInt64();
        again.EventType.ShouldBe("ledger.funds_transferred");

        (await factory.BalanceAsync(recipient.WalletId)).ShouldBe(50_000);
        (await factory.BalanceAsync(sender.WalletId)).ShouldBe(100_000 - 50_000 - charges);
        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task A_refused_transfer_stays_refused_even_after_the_sender_has_the_money()
    {
        var sender = await factory.CustomerAsync(rupees: 100);
        var recipient = await factory.CustomerAsync(rupees: 0);
        var command = new TransferFunds(Guid.CreateVersion7(), Guid.CreateVersion7(), sender.Id.Value, recipient.Phone, 50_000, null);

        var refused = await SendAsync(command);
        refused.EventType.ShouldBe("ledger.funds_transfer_rejected");
        var code = refused.Payload.GetProperty("code").GetString();
        code.ShouldNotBeNullOrWhiteSpace();

        // The sender tops up, and a late copy of the same command arrives: still refused, nothing moves.
        await factory.CashInAsync(sender.WalletId, rupees: 1_000);
        var late = await SendAsync(command, nth: 2);
        late.EventType.ShouldBe("ledger.funds_transfer_rejected");
        late.Payload.GetProperty("code").GetString().ShouldBe(code);
        (await factory.BalanceAsync(recipient.WalletId)).ShouldBe(0);
        (await factory.BalanceAsync(sender.WalletId)).ShouldBe(110_000);
        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task Unknown_recipients_and_sending_to_yourself_are_refused()
    {
        var sender = await factory.CustomerAsync(rupees: 1_000);

        var unknown = await SendAsync(new TransferFunds(Guid.CreateVersion7(), Guid.CreateVersion7(), sender.Id.Value, "+923009999998", 10_000, null));
        var self = await SendAsync(new TransferFunds(Guid.CreateVersion7(), Guid.CreateVersion7(), sender.Id.Value, sender.Phone, 10_000, null));

        unknown.Payload.GetProperty("code").GetString().ShouldBe("wallet.recipient_not_found");
        self.EventType.ShouldBe("ledger.funds_transfer_rejected");
        (await factory.BalanceAsync(sender.WalletId)).ShouldBe(100_000);
    }
}
