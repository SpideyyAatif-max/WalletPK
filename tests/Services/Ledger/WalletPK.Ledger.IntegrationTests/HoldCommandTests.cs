using Microsoft.Extensions.DependencyInjection;
using WalletPK.Common.Identifiers;
using WalletPK.Contracts.Ledger;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using WalletPK.Messaging;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>
/// M6: Sagas reserve, settle and return customers' money with commands on ledger.commands; the Ledger
/// answers on ledger.replies. Real Kafka, real inbox and outbox, and every command repeated to prove
/// it is safe to repeat.
/// </summary>
[Collection(LedgerCollection.Name)]
public class HoldCommandTests(LedgerApiFactory factory)
{
    private IntegrationEventRegistry Registry => factory.Services.GetRequiredService<IntegrationEventRegistry>();

    /// <summary>Sends a command and waits for the Ledger's <paramref name="nth"/> answer about that hold.</summary>
    private async Task<ReadMessage> SendAsync(IntegrationEvent command, Guid holdId, int nth = 1)
    {
        await factory.PublishAsync(command, Registry, correlationId: $"saga-{holdId}");
        bool Mine(ReadMessage message) => message.Key == holdId.ToString();
        return factory.ReadUntil(KafkaTopics.LedgerReplies, read => read.Count(Mine) >= nth, TimeSpan.FromSeconds(30)).Where(Mine).ElementAt(nth - 1);
    }

    private static PlaceHold Place(Customer customer, Guid holdId, long paisa) =>
        new(holdId, customer.Id.Value, paisa, "BankTransfer", "Bank transfer to HBL", $"transfer-{holdId}", 3_600, "device-1");

    private static CaptureHold Capture(Guid holdId, long paisa) =>
        new(holdId, holdId, "BankTransfer", "Bank transfer to HBL", [new HoldCredit("BANK_FLOAT", paisa)]);

    [Fact]
    public async Task A_placed_hold_reserves_the_money_once_however_often_it_is_asked_for()
    {
        var customer = await factory.CustomerAsync(rupees: 1_000);
        var holdId = Guid.CreateVersion7();

        var placed = await SendAsync(Place(customer, holdId, 40_000), holdId);
        placed.EventType.ShouldBe("ledger.hold_placed");
        placed.Payload.GetProperty("walletId").GetGuid().ShouldBe(customer.WalletId.Value);
        placed.Envelope.CorrelationId.ShouldBe($"saga-{holdId}");

        var again = await SendAsync(Place(customer, holdId, 40_000), holdId, nth: 2);
        again.EventType.ShouldBe("ledger.hold_placed");

        var wallet = await JsonAsync(await customer.Client.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel));
        Paisa(wallet, "held").ShouldBe(40_000);
        Paisa(wallet, "available").ShouldBe(60_000);
        factory.Limits.Reserved[new TransactionId(holdId)].MinorUnits.ShouldBe(40_000);
    }

    [Fact]
    public async Task A_hold_the_wallet_cannot_cover_or_the_limits_refuse_is_rejected()
    {
        var poor = await factory.CustomerAsync(rupees: 100);
        var tooMuch = Guid.CreateVersion7();
        var rejected = await SendAsync(Place(poor, tooMuch, 20_000), tooMuch);
        rejected.EventType.ShouldBe("ledger.hold_rejected");
        rejected.Payload.GetProperty("code").GetString().ShouldBe("ledger.insufficient_funds");
        factory.Limits.Released.ContainsKey(new TransactionId(tooMuch)).ShouldBeTrue("the limit reservation is given back");

        var limited = await factory.CustomerAsync(rupees: 1_000);
        factory.Limits.Refuse(limited.Id, "ExceedsDaily", "Daily limit reached.");
        var overLimit = Guid.CreateVersion7();
        var refused = await SendAsync(Place(limited, overLimit, 10_000), overLimit);
        refused.Payload.GetProperty("code").GetString().ShouldBe("wallet.limit_exceeded");
        refused.Payload.GetProperty("reason").GetString().ShouldBe("Daily limit reached.");

        var nobody = Guid.CreateVersion7();
        var noWallet = await SendAsync(new PlaceHold(nobody, Guid.CreateVersion7(), 100, "BankTransfer", "x", null, 600, null), nobody);
        noWallet.Payload.GetProperty("code").GetString().ShouldBe("wallet.not_found");
    }

    [Fact]
    public async Task Capturing_moves_the_money_to_the_bank_float_exactly_once()
    {
        var customer = await factory.CustomerAsync(rupees: 1_000);
        var holdId = Guid.CreateVersion7();
        await SendAsync(Place(customer, holdId, 25_000), holdId);
        var floatBefore = await factory.SystemBalanceAsync(ChartOfAccounts.BankFloat);

        var captured = await SendAsync(Capture(holdId, 25_000), holdId, nth: 2);
        captured.EventType.ShouldBe("ledger.hold_captured");
        captured.Payload.GetProperty("transactionId").GetGuid().ShouldBe(holdId);

        (await SendAsync(Capture(holdId, 25_000), holdId, nth: 3)).EventType.ShouldBe("ledger.hold_captured");

        (await factory.BalanceAsync(customer.WalletId)).ShouldBe(75_000);
        (await factory.SystemBalanceAsync(ChartOfAccounts.BankFloat)).ShouldBe(floatBefore - 25_000);
        var wallet = await JsonAsync(await customer.Client.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel));
        Paisa(wallet, "held").ShouldBe(0);

        // Too late to release: the answer says the money already left.
        (await SendAsync(new ReleaseHold(holdId, "customer cancelled"), holdId, nth: 4)).EventType.ShouldBe("ledger.hold_captured");
        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task Releasing_returns_the_money_and_saying_it_again_changes_nothing()
    {
        var customer = await factory.CustomerAsync(rupees: 1_000);
        var holdId = Guid.CreateVersion7();
        await SendAsync(Place(customer, holdId, 30_000), holdId);

        var released = await SendAsync(new ReleaseHold(holdId, "bank rejected the transfer"), holdId, nth: 2);
        released.EventType.ShouldBe("ledger.hold_released");
        released.Payload.GetProperty("status").GetString().ShouldBe("Released");
        factory.Limits.Released.ContainsKey(new TransactionId(holdId)).ShouldBeTrue();

        (await SendAsync(new ReleaseHold(holdId, "again"), holdId, nth: 3)).Payload.GetProperty("status").GetString().ShouldBe("Released");
        var wallet = await JsonAsync(await customer.Client.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel));
        Paisa(wallet, "held").ShouldBe(0);
        Paisa(wallet, "balance").ShouldBe(100_000);

        // A capture after the release fails, and says why.
        var late = await SendAsync(Capture(holdId, 30_000), holdId, nth: 4);
        late.EventType.ShouldBe("ledger.hold_capture_failed");
        late.Payload.GetProperty("code").GetString().ShouldBe("ledger.hold_not_active");

        var never = Guid.CreateVersion7();
        (await SendAsync(new ReleaseHold(never, "never placed"), never)).Payload.GetProperty("status").GetString().ShouldBe("NotPlaced");
    }

    [Fact]
    public async Task Bad_commands_get_clear_answers_instead_of_blocking_the_partition()
    {
        var customer = await factory.CustomerAsync(rupees: 100);
        var holdId = Guid.CreateVersion7();

        (await SendAsync(Place(customer, holdId, 1_000) with { Purpose = "Lottery" }, holdId)).Payload.GetProperty("code").GetString().ShouldBe("ledger.invalid_purpose");

        var placed = Guid.CreateVersion7();
        await SendAsync(Place(customer, placed, 1_000), placed);
        var wrongAccount = await SendAsync(new CaptureHold(placed, placed, "BankTransfer", "x", [new HoldCredit("CASH_DRAWER", 1_000)]), placed, nth: 2);
        wrongAccount.EventType.ShouldBe("ledger.hold_capture_failed");
        wrongAccount.Payload.GetProperty("code").GetString().ShouldBe("ledger.invalid_credit");
    }
}
