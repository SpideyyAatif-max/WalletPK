using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Application.Events;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.ReadModels;
using LedgerContracts = WalletPK.Contracts.Ledger;

namespace WalletPK.Ledger.UnitTests.Application;

/// <summary>The pieces of the read side (M4.4-M4.6) that need no database or broker.</summary>
public class ReadSideTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Opening_a_wallet_announces_it_keyed_by_the_wallet()
    {
        var ownerId = UserId.New();
        var wallet = Account.OpenWallet(ownerId, AccountType.MerchantWallet, "Shop", _now, "+923001234567");

        var opened = wallet.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<WalletOpened>();
        var published = LedgerIntegrationEvents.FromDomainEvent(opened).ShouldHaveSingleItem().ShouldBeOfType<LedgerContracts.WalletOpened>();

        published.WalletId.ShouldBe(wallet.Id.Value);
        published.OwnerId.ShouldBe(ownerId.Value);
        published.WalletNumber.ShouldBe("+923001234567");
        published.WalletType.ShouldBe("MerchantWallet");
        published.OpenedAt.ShouldBe(_now);
        published.EventId.ShouldBe(opened.EventId);
        published.GetPartitionKey().ShouldBe(wallet.Id.ToString());
    }

    [Theory]
    [InlineData(1, "ledger-read-models-g1")]
    [InlineData(42, "ledger-read-models-g42")]
    public void A_generation_and_its_consumer_group_name_round_trip(long generation, string group)
    {
        var options = new ReadModelOptions();

        options.ConsumerGroupFor(generation).ShouldBe(group);
        options.GenerationOf(group).ShouldBe(generation);
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("ledger-read-models-g")]
    [InlineData("ledger-read-models-gx")]
    [InlineData("ledger-read-models-g-1")]
    [InlineData("other-read-models-g1")]
    public void Other_consumer_groups_are_not_a_generation(string group) =>
        new ReadModelOptions().GenerationOf(group).ShouldBeNull();

    [Theory]
    [InlineData(JournalType.P2PTransfer, EntryDirection.Debit, "+923001234567", "Sent to +92300*****67")]
    [InlineData(JournalType.P2PTransfer, EntryDirection.Credit, "+923001234567", "Received from +92300*****67")]
    [InlineData(JournalType.P2PTransfer, EntryDirection.Debit, null, "Wallet to wallet transfer")]
    [InlineData(JournalType.CashIn, EntryDirection.Credit, null, "Cash-in")]
    [InlineData(JournalType.CashOut, EntryDirection.Debit, null, "Cash-out")]
    [InlineData(JournalType.Adjustment, EntryDirection.Credit, null, "Wallet to wallet transfer")]
    public void Statement_titles_name_the_other_wallet_masked(JournalType type, EntryDirection direction, string? other, string title) =>
        StatementTitles.For(type, "Wallet to wallet transfer", direction, other).ShouldBe(title);
}
