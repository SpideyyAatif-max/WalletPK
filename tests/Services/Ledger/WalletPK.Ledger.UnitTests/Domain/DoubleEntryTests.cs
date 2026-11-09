using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Domain;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.UnitTests.Domain;

public class DoubleEntryTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static Account Wallet(string name = "Ali") => Account.OpenWallet(UserId.New(), AccountType.CustomerWallet, name, _now);

    private static Account BankFloat() => Account.OpenSystemShard(ChartOfAccounts.BankFloat, 0, _now);

    private static Account FeeIncome() => Account.OpenSystemShard(ChartOfAccounts.FeeIncome, 0, _now);

    private static JournalTransaction Post(params JournalLine[] lines) =>
        JournalTransaction.Post(TransactionId.New(), JournalType.Adjustment, "test", null, lines, _now).Value;

    /// <summary>Puts money into a wallet the way a real cash-in does: debit the bank float, credit the wallet.</summary>
    private static void CashIn(Account wallet, long paisa) =>
        Post(new JournalLine(BankFloat(), EntryDirection.Debit, paisa), new JournalLine(wallet, EntryDirection.Credit, paisa));

    [Fact]
    public void A_wallet_is_credit_normal_and_can_never_go_negative()
    {
        var wallet = Wallet();

        wallet.NormalBalance.ShouldBe(NormalBalance.Credit);
        wallet.AllowNegative.ShouldBeFalse();
        wallet.SignedAmount(EntryDirection.Credit, 500).ShouldBe(500);
        wallet.SignedAmount(EntryDirection.Debit, 500).ShouldBe(-500);
        wallet.CanApply(-1).Error.Code.ShouldBe("ledger.insufficient_funds");
    }

    [Fact]
    public void A_cash_in_raises_both_the_bank_float_asset_and_the_wallet_liability()
    {
        var bank = BankFloat();
        var wallet = Wallet();

        var journal = Post(new JournalLine(bank, EntryDirection.Debit, 100_000), new JournalLine(wallet, EntryDirection.Credit, 100_000));

        bank.Balance.ShouldBe(100_000);
        wallet.Balance.ShouldBe(100_000);
        journal.Amount.ShouldBe(100_000);
        journal.Entries.Select(entry => entry.BalanceAfter).ShouldBe(new long[] { 100_000, 100_000 });
        journal.Entries.Select(entry => entry.LineNumber).ShouldBe(new short[] { 1, 2 });
        journal.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<JournalPosted>();
    }

    [Fact]
    public void A_transfer_with_a_fee_moves_money_without_creating_any()
    {
        var ali = Wallet("Ali");
        var sara = Wallet("Sara");
        var fees = FeeIncome();
        CashIn(ali, 100_000);

        // Ali sends Rs 500 to Sara and pays a Rs 10 fee.
        Post(
            new JournalLine(ali, EntryDirection.Debit, 51_000),
            new JournalLine(sara, EntryDirection.Credit, 50_000),
            new JournalLine(fees, EntryDirection.Credit, 1_000));

        ali.Balance.ShouldBe(49_000);
        sara.Balance.ShouldBe(50_000);
        fees.Balance.ShouldBe(1_000);
        (ali.Balance + sara.Balance + fees.Balance).ShouldBe(100_000);
    }

    [Fact]
    public void An_overdraft_is_refused_and_no_account_changes()
    {
        var ali = Wallet("Ali");
        var sara = Wallet("Sara");
        CashIn(ali, 10_000);

        // Two lines: the credit to Sara comes first, so a naive line-by-line engine would already have changed her balance.
        var result = JournalTransaction.Post(
            TransactionId.New(),
            JournalType.P2PTransfer,
            "too much",
            null,
            [new JournalLine(sara, EntryDirection.Credit, 20_000), new JournalLine(ali, EntryDirection.Debit, 20_000)],
            _now);

        result.Error.Code.ShouldBe("ledger.insufficient_funds");
        ali.Balance.ShouldBe(10_000);
        sara.Balance.ShouldBe(0);
    }

    [Fact]
    public void The_net_change_per_account_is_what_counts()
    {
        var ali = Wallet();
        CashIn(ali, 10_000);

        // Debit 15,000 and credit 6,000 in the same journal: net -9,000 fits a 10,000 balance.
        var journal = JournalTransaction.Post(
            TransactionId.New(),
            JournalType.Adjustment,
            "net",
            null,
            [
                new JournalLine(ali, EntryDirection.Debit, 15_000),
                new JournalLine(ali, EntryDirection.Credit, 6_000),
                new JournalLine(FeeIncome(), EntryDirection.Credit, 9_000),
            ],
            _now);

        journal.IsSuccess.ShouldBeTrue();
        ali.Balance.ShouldBe(1_000);
    }

    [Fact]
    public void Unbalanced_or_malformed_journals_are_rejected()
    {
        var wallet = Wallet();
        var bank = BankFloat();
        var id = TransactionId.New();

        JournalTransaction.Post(id, JournalType.CashIn, "x", null, [new JournalLine(bank, EntryDirection.Debit, 500), new JournalLine(wallet, EntryDirection.Credit, 400)], _now)
            .Error.ShouldBe(LedgerErrors.Unbalanced);
        JournalTransaction.Post(id, JournalType.CashIn, "x", null, [new JournalLine(bank, EntryDirection.Debit, 500)], _now)
            .Error.ShouldBe(LedgerErrors.TooFewLines);
        JournalTransaction.Post(id, JournalType.CashIn, "x", null, [new JournalLine(bank, EntryDirection.Debit, 0), new JournalLine(wallet, EntryDirection.Credit, 0)], _now)
            .Error.ShouldBe(LedgerErrors.AmountInvalid);
        JournalTransaction.Post(id, JournalType.CashIn, " ", null, [new JournalLine(bank, EntryDirection.Debit, 5), new JournalLine(wallet, EntryDirection.Credit, 5)], _now)
            .Error.ShouldBe(LedgerErrors.DescriptionRequired);
        JournalTransaction.Post(id, JournalType.CashIn, "x", null, [new JournalLine(bank, EntryDirection.Credit, 5), new JournalLine(wallet, EntryDirection.Credit, 5)], _now)
            .Error.ShouldBe(LedgerErrors.Unbalanced);

        wallet.Balance.ShouldBe(0);
        bank.Balance.ShouldBe(0);
    }

    [Fact]
    public void A_frozen_wallet_can_receive_but_not_send()
    {
        var wallet = Wallet();
        CashIn(wallet, 5_000);
        wallet.Freeze(_now);

        wallet.CanApply(1_000).IsSuccess.ShouldBeTrue();
        wallet.CanApply(-1_000).Error.Code.ShouldBe("ledger.account_frozen");
    }

    [Fact]
    public void System_accounts_may_go_negative_because_a_single_shard_can()
    {
        var shard = FeeIncome();

        shard.AllowNegative.ShouldBeTrue();
        shard.CanApply(-1_000).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Arithmetic_overflow_throws_instead_of_wrapping()
    {
        var bank = BankFloat();
        var wallet = Wallet();

        Should.Throw<OverflowException>(() => JournalTransaction.Post(
            TransactionId.New(),
            JournalType.CashIn,
            "overflow",
            null,
            [
                new JournalLine(bank, EntryDirection.Debit, long.MaxValue),
                new JournalLine(bank, EntryDirection.Debit, 1),
                new JournalLine(wallet, EntryDirection.Credit, long.MaxValue),
            ],
            _now));
    }
}
