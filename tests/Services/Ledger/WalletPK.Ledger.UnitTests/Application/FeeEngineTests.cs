using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Fees;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.UnitTests.Application;

public class FeeEngineTests
{
    /// <summary>The default P2P schedule from appsettings.json: free up to Rs. 1,000, then 0.5% (Rs. 5 to Rs. 50).</summary>
    private static FeeEngine Engine(decimal taxPercent = 16) => new(new FeeOptions
    {
        TaxPercent = taxPercent,
        Schedules =
        {
            ["P2PTransfer"] =
            [
                new FeeBand { UpToRupees = 1_000, FlatRupees = 0 },
                new FeeBand { Percent = 0.5m, MinRupees = 5, MaxRupees = 50 },
            ],
            ["CashOut"] =
            [
                new FeeBand { UpToRupees = 5_000, FlatRupees = 20 },
                new FeeBand { FlatRupees = 10, Percent = 1, MaxRupees = 200 },
            ],
        },
    });

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1_000, 0, 0)]                // top of the free band
    [InlineData(1_000.01, 5.00, 0.80)]       // 0.5% is Rs. 5.00005 → Rs. 5.00
    [InlineData(1_001, 5.01, 0.80)]          // 0.5% = Rs. 5.005 → rounded half away from zero
    [InlineData(1_500, 7.50, 1.20)]
    [InlineData(10_000, 50, 8)]              // exactly the cap
    [InlineData(250_000, 50, 8)]             // capped
    public void P2P_fee_follows_the_bands_and_tax_is_16_percent_of_the_fee(decimal amount, decimal fee, decimal tax)
    {
        var quote = Engine().Quote(JournalType.P2PTransfer, Money.FromRupees(amount));

        quote.Amount.ShouldBe(Money.FromRupees(amount));
        quote.Fee.ShouldBe(Money.FromRupees(fee));
        quote.Tax.ShouldBe(Money.FromRupees(tax));
        quote.TotalDebit.ShouldBe(Money.FromRupees(amount + fee + tax));
        quote.IsFree.ShouldBe(fee == 0);
    }

    [Fact]
    public void Below_the_minimum_the_minimum_fee_applies()
    {
        // 0.5% of Rs. 1,000.50 is about Rs. 5; any amount just over the free band pays at least Rs. 5.
        Engine().Quote(JournalType.P2PTransfer, Money.FromRupees(1_000.50m)).Fee.ShouldBe(Money.FromRupees(5));
    }

    [Theory]
    [InlineData(3_000, 20)]       // flat
    [InlineData(10_000, 110)]     // Rs. 10 + 1%
    [InlineData(50_000, 200)]     // capped
    public void Flat_and_percent_fees_combine(decimal amount, decimal fee) =>
        Engine().Quote(JournalType.CashOut, Money.FromRupees(amount)).Fee.ShouldBe(Money.FromRupees(fee));

    [Fact]
    public void A_type_without_a_schedule_is_free()
    {
        var quote = Engine().Quote(JournalType.CashIn, Money.FromRupees(5_000));

        quote.IsFree.ShouldBeTrue();
        quote.TotalDebit.ShouldBe(Money.FromRupees(5_000));
    }

    [Fact]
    public void Tax_rate_is_configurable()
    {
        Engine(taxPercent: 0).Quote(JournalType.P2PTransfer, Money.FromRupees(1_500)).Tax.ShouldBe(Money.Zero());
    }

    [Fact]
    public void Fee_rules_are_validated()
    {
        new FeeOptions().IsValid().ShouldBeTrue();
        new FeeOptions { TaxPercent = -1 }.IsValid().ShouldBeFalse();
        new FeeOptions { Schedules = { ["NoSuchType"] = [new FeeBand()] } }.IsValid().ShouldBeFalse();
        new FeeOptions { Schedules = { ["P2PTransfer"] = [] } }.IsValid().ShouldBeFalse();

        // Only the last band may be open-ended, and bands must go up.
        new FeeOptions { Schedules = { ["P2PTransfer"] = [new FeeBand(), new FeeBand { UpToRupees = 10 }] } }.IsValid().ShouldBeFalse();
        new FeeOptions { Schedules = { ["P2PTransfer"] = [new FeeBand { UpToRupees = 100 }, new FeeBand { UpToRupees = 50 }] } }.IsValid().ShouldBeFalse();
        new FeeOptions { Schedules = { ["P2PTransfer"] = [new FeeBand { MinRupees = 10, MaxRupees = 5 }] } }.IsValid().ShouldBeFalse();
    }
}

public class TransferTransactionIdTests
{
    [Fact]
    public void Same_user_and_key_always_give_the_same_transaction()
    {
        var user = UserId.New();

        WalletService.TransactionIdFor(user, "key-12345678").ShouldBe(WalletService.TransactionIdFor(user, "key-12345678"));
    }

    [Fact]
    public void Different_users_or_keys_give_different_transactions()
    {
        var user = UserId.New();

        WalletService.TransactionIdFor(user, "key-12345678").ShouldNotBe(WalletService.TransactionIdFor(user, "key-87654321"));
        WalletService.TransactionIdFor(user, "key-12345678").ShouldNotBe(WalletService.TransactionIdFor(UserId.New(), "key-12345678"));
    }

    [Fact]
    public void Transaction_ids_are_marked_as_version_8_uuids()
    {
        var id = WalletService.TransactionIdFor(UserId.New(), "key-12345678").Value;

        id.Version.ShouldBe(8);
        (id.Variant & 0b1100).ShouldBe(0b1000);
    }
}
