using Microsoft.Extensions.DependencyInjection;
using WalletPK.Contracts.Ledger;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using WalletPK.Messaging;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>M8: merchant wallets, QR payments with the fee split off, and nightly settlement, over ledger.commands.</summary>
[Collection(LedgerCollection.Name)]
public class MerchantCommandTests(LedgerApiFactory factory)
{
    private IntegrationEventRegistry Registry => factory.Services.GetRequiredService<IntegrationEventRegistry>();

    private async Task<ReadMessage> SendAsync(IntegrationEvent command, string key, int nth = 1)
    {
        await factory.PublishAsync(command, Registry, correlationId: $"merchant-{key}");
        bool Mine(ReadMessage message) => message.Key == key;
        return factory.ReadUntil(KafkaTopics.LedgerReplies, read => read.Count(Mine) >= nth, TimeSpan.FromSeconds(30)).Where(Mine).ElementAt(nth - 1);
    }

    private async Task<(Customer Owner, Guid MerchantId, Guid WalletId)> MerchantAsync()
    {
        var owner = await factory.CustomerAsync(rupees: 0);
        var merchantId = Guid.CreateVersion7();
        var code = $"WPKM{Random.Shared.Next(100_000, 999_999)}";
        var opened = await SendAsync(new OpenMerchantWallet(merchantId, owner.Id.Value, code, "Ali Tea Stall"), merchantId.ToString());
        opened.EventType.ShouldBe("ledger.merchant_wallet_opened");
        return (owner, merchantId, opened.Payload.GetProperty("walletId").GetGuid());
    }

    [Fact]
    public async Task Opening_a_merchant_wallet_twice_gives_the_same_wallet()
    {
        var (owner, merchantId, walletId) = await MerchantAsync();

        var again = await SendAsync(new OpenMerchantWallet(merchantId, owner.Id.Value, "WPKM000001", "Ali Tea Stall"), merchantId.ToString(), nth: 2);

        again.Payload.GetProperty("walletId").GetGuid().ShouldBe(walletId);
        walletId.ShouldNotBe(owner.WalletId.Value);
    }

    [Fact]
    public async Task A_qr_payment_splits_the_fee_off_in_one_journal_and_settlement_sweeps_the_net_to_the_bank()
    {
        var (owner, _, merchantWallet) = await MerchantAsync();
        var payer = await factory.CustomerAsync(rupees: 2_000);
        var feeBefore = await factory.SystemBalanceAsync(ChartOfAccounts.FeeIncome);
        var taxBefore = await factory.SystemBalanceAsync(ChartOfAccounts.TaxPayable);

        // Rs. 1,000: fee Rs. 10, tax Rs. 1.60, the merchant gets Rs. 988.40.
        var paymentId = Guid.CreateVersion7();
        PayMerchant Pay() => new(paymentId, paymentId, payer.Id.Value, owner.Id.Value, 100_000, 1_000, 160, "QR payment to Ali Tea Stall", "device-1");
        var paid = await SendAsync(Pay(), paymentId.ToString());
        var again = await SendAsync(Pay(), paymentId.ToString(), nth: 2);

        paid.EventType.ShouldBe("ledger.merchant_paid");
        paid.Payload.GetProperty("netPaisa").GetInt64().ShouldBe(98_840);
        again.EventType.ShouldBe("ledger.merchant_paid");
        (await factory.BalanceAsync(payer.WalletId)).ShouldBe(100_000);
        (await factory.BalanceAsync(new(merchantWallet))).ShouldBe(98_840);
        (await factory.SystemBalanceAsync(ChartOfAccounts.FeeIncome) - feeBefore).ShouldBe(1_000);
        (await factory.SystemBalanceAsync(ChartOfAccounts.TaxPayable) - taxBefore).ShouldBe(160);

        // The night's settlement moves the net sales to the bank, once.
        var settlementId = Guid.CreateVersion7();
        SettleMerchant Settle() => new(settlementId, Guid.Empty, owner.Id.Value, 98_840, "Settlement 2026-11-18");
        var settled = await SendAsync(Settle(), settlementId.ToString());
        await SendAsync(Settle(), settlementId.ToString(), nth: 2);

        settled.EventType.ShouldBe("ledger.merchant_settled");
        (await factory.BalanceAsync(new(merchantWallet))).ShouldBe(0);
        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task A_payment_the_payer_cannot_cover_is_refused_for_good()
    {
        var (owner, _, merchantWallet) = await MerchantAsync();
        var payer = await factory.CustomerAsync(rupees: 100);
        var paymentId = Guid.CreateVersion7();
        PayMerchant Pay() => new(paymentId, paymentId, payer.Id.Value, owner.Id.Value, 50_000, 500, 80, "QR payment", null);

        var refused = await SendAsync(Pay(), paymentId.ToString());
        await factory.CashInAsync(payer.WalletId, rupees: 1_000);
        var late = await SendAsync(Pay(), paymentId.ToString(), nth: 2);

        refused.EventType.ShouldBe("ledger.funds_transfer_rejected");
        late.EventType.ShouldBe("ledger.funds_transfer_rejected");
        (await factory.BalanceAsync(new(merchantWallet))).ShouldBe(0);
    }
}
