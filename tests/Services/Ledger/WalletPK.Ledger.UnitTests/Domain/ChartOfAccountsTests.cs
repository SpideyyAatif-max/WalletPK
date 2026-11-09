using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Domain.Accounts;

namespace WalletPK.Ledger.UnitTests.Domain;

public class ChartOfAccountsTests
{
    [Fact]
    public void The_chart_has_the_five_system_accounts_with_the_expected_sides()
    {
        ChartOfAccounts.SystemAccounts.Select(account => account.Code)
            .ShouldBe(new[] { "BANK_FLOAT", "FEE_INCOME", "TAX_PAYABLE", "BILLER_SETTLEMENT", "SUSPENSE" });
        ChartOfAccounts.BankFloat.NormalBalance.ShouldBe(NormalBalance.Debit);
        ChartOfAccounts.FeeIncome.NormalBalance.ShouldBe(NormalBalance.Credit);
        ChartOfAccounts.FeeIncome.Shards.ShouldBe(8);
        ChartOfAccounts.SystemAccounts.Sum(account => account.Shards).ShouldBe(21);
    }

    [Fact]
    public void System_account_ids_are_fixed_and_unique()
    {
        var ids = ChartOfAccounts.SystemAccounts
            .SelectMany(account => Enumerable.Range(0, account.Shards).Select(shard => ChartOfAccounts.IdFor(account.Code, shard)))
            .ToList();

        ids.Distinct().Count().ShouldBe(21);
        ChartOfAccounts.IdFor("FEE_INCOME", 3).ShouldBe(ChartOfAccounts.IdFor("FEE_INCOME", 3));
        ChartOfAccounts.IdFor("FEE_INCOME", 3).ShouldNotBe(ChartOfAccounts.IdFor("FEE_INCOME", 4));
    }

    [Fact]
    public void A_transaction_always_maps_to_the_same_shard_and_traffic_spreads_over_all_of_them()
    {
        var transaction = TransactionId.New();
        var first = ChartOfAccounts.ShardFor(ChartOfAccounts.FeeIncome, transaction);

        ChartOfAccounts.ShardFor(ChartOfAccounts.FeeIncome, transaction).ShouldBe(first);

        var used = Enumerable.Range(0, 2_000)
            .Select(_ => ChartOfAccounts.ShardFor(ChartOfAccounts.FeeIncome, TransactionId.New()))
            .GroupBy(shard => shard)
            .ToDictionary(group => group.Key, group => group.Count());

        used.Keys.Order().ShouldBe(Enumerable.Range(0, 8));
        used.Values.ShouldAllBe(count => count > 150); // roughly 250 each; never all on one row
        ChartOfAccounts.ShardFor(ChartOfAccounts.Suspense, transaction).ShouldBe(0);
    }

    [Fact]
    public void System_shards_are_opened_with_their_fixed_id_and_unknown_codes_fail()
    {
        var shard = Account.OpenSystemShard(ChartOfAccounts.TaxPayable, 2, DateTimeOffset.UnixEpoch);

        shard.Id.ShouldBe(ChartOfAccounts.IdFor("TAX_PAYABLE", 2));
        shard.SystemCode.ShouldBe("TAX_PAYABLE");
        shard.Name.ShouldBe("Tax payable (shard 2)");
        ChartOfAccounts.Get("SUSPENSE").ShouldBe(ChartOfAccounts.Suspense);
        Should.Throw<ArgumentException>(() => ChartOfAccounts.Get("PETTY_CASH"));
        Should.Throw<ArgumentOutOfRangeException>(() => Account.OpenSystemShard(ChartOfAccounts.Suspense, 1, DateTimeOffset.UnixEpoch));
    }
}
