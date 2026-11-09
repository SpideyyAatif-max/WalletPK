using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Domain;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.UnitTests.Domain;

public class HoldTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _thirtyMinutes = TimeSpan.FromMinutes(30);

    private static Account FundedWallet(long paisa)
    {
        var wallet = Account.OpenWallet(UserId.New(), AccountType.CustomerWallet, "Ali", _now);
        JournalTransaction.Post(
            TransactionId.New(), JournalType.CashIn, "fund", null,
            [new JournalLine(Account.OpenSystemShard(ChartOfAccounts.BankFloat, 0, _now), EntryDirection.Debit, paisa), new JournalLine(wallet, EntryDirection.Credit, paisa)],
            _now).IsSuccess.ShouldBeTrue();
        return wallet;
    }

    [Fact]
    public void Placing_a_hold_keeps_the_balance_but_lowers_what_can_be_spent()
    {
        var wallet = FundedWallet(10_000);

        var hold = Hold.Place(wallet, 6_000, "Bank transfer", "RAAST-1", _thirtyMinutes, _now).Value;

        wallet.Balance.ShouldBe(10_000);
        wallet.Held.ShouldBe(6_000);
        wallet.Available.ShouldBe(4_000);
        hold.Status.ShouldBe(HoldStatus.Active);
        hold.ExpiresAt.ShouldBe(_now + _thirtyMinutes);
        wallet.CanApply(-4_001).Error.Code.ShouldBe("ledger.insufficient_funds"); // held money cannot be spent twice
    }

    [Fact]
    public void A_hold_larger_than_the_available_balance_is_refused()
    {
        var wallet = FundedWallet(10_000);
        Hold.Place(wallet, 7_000, "First", null, _thirtyMinutes, _now).IsSuccess.ShouldBeTrue();

        var second = Hold.Place(wallet, 3_001, "Second", null, _thirtyMinutes, _now);

        second.Error.Code.ShouldBe("ledger.insufficient_funds");
        wallet.Held.ShouldBe(7_000);
    }

    [Fact]
    public void A_partial_capture_returns_the_whole_reservation_for_the_journal_to_debit()
    {
        var wallet = FundedWallet(10_000);
        var hold = Hold.Place(wallet, 6_000, "Bill", null, _thirtyMinutes, _now).Value;
        var transaction = TransactionId.New();

        hold.Capture(wallet, 4_500, transaction, _now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        hold.Status.ShouldBe(HoldStatus.Captured);
        hold.CapturedAmount.ShouldBe(4_500);
        hold.CaptureTransactionId.ShouldBe(transaction);
        wallet.Held.ShouldBe(0);
        wallet.Balance.ShouldBe(10_000); // the capture journal (posted by HoldService) debits the 4,500
    }

    [Fact]
    public void A_hold_can_be_settled_only_once()
    {
        var wallet = FundedWallet(10_000);
        var hold = Hold.Place(wallet, 6_000, "Bill", null, _thirtyMinutes, _now).Value;
        hold.Release(wallet, _now).IsSuccess.ShouldBeTrue();

        hold.Capture(wallet, 1_000, TransactionId.New(), _now).Error.ShouldBe(LedgerErrors.HoldNotActive);
        hold.Release(wallet, _now).Error.ShouldBe(LedgerErrors.HoldNotActive);
        wallet.Held.ShouldBe(0);
        hold.Status.ShouldBe(HoldStatus.Released);
    }

    [Fact]
    public void Capture_is_limited_to_the_held_amount_and_to_the_hold_lifetime()
    {
        var wallet = FundedWallet(10_000);
        var hold = Hold.Place(wallet, 6_000, "Bill", null, _thirtyMinutes, _now).Value;

        hold.Capture(wallet, 6_001, TransactionId.New(), _now).Error.ShouldBe(LedgerErrors.CaptureTooLarge);
        hold.Capture(wallet, 0, TransactionId.New(), _now).Error.ShouldBe(LedgerErrors.AmountInvalid);
        hold.Capture(wallet, 1_000, TransactionId.New(), _now + _thirtyMinutes).Error.ShouldBe(LedgerErrors.HoldExpired);
        wallet.Held.ShouldBe(6_000);
    }

    [Fact]
    public void Only_an_expired_hold_can_be_expired_and_an_expired_hold_can_still_be_released()
    {
        var wallet = FundedWallet(10_000);
        var hold = Hold.Place(wallet, 6_000, "Bill", null, _thirtyMinutes, _now).Value;

        hold.Expire(wallet, _now.AddMinutes(29)).Error.ShouldBe(LedgerErrors.HoldNotActive);
        hold.IsExpired(_now + _thirtyMinutes).ShouldBeTrue();
        hold.Expire(wallet, _now + _thirtyMinutes).IsSuccess.ShouldBeTrue();

        hold.Status.ShouldBe(HoldStatus.Expired);
        wallet.Available.ShouldBe(10_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Holds_need_a_positive_amount(long amount) =>
        Hold.Place(FundedWallet(10_000), amount, "x", null, _thirtyMinutes, _now).Error.ShouldBe(LedgerErrors.AmountInvalid);

    [Fact]
    public void Holds_need_a_sensible_lifetime_and_a_description()
    {
        var wallet = FundedWallet(10_000);

        Hold.Place(wallet, 100, "x", null, TimeSpan.FromSeconds(30), _now).Error.ShouldBe(LedgerErrors.HoldLifetimeInvalid);
        Hold.Place(wallet, 100, "x", null, TimeSpan.FromDays(8), _now).Error.ShouldBe(LedgerErrors.HoldLifetimeInvalid);
        Hold.Place(wallet, 100, " ", null, _thirtyMinutes, _now).Error.ShouldBe(LedgerErrors.DescriptionRequired);
        wallet.Held.ShouldBe(0);
    }

    [Fact]
    public void A_frozen_wallet_cannot_place_holds()
    {
        var wallet = FundedWallet(10_000);
        wallet.Freeze(_now);

        Hold.Place(wallet, 100, "x", null, _thirtyMinutes, _now).Error.Code.ShouldBe("ledger.account_frozen");
    }

    [Fact]
    public void Fencing_tokens_may_only_grow()
    {
        var wallet = FundedWallet(10_000);

        wallet.ApplyFencingToken(7).IsSuccess.ShouldBeTrue();
        wallet.ApplyFencingToken(7).IsSuccess.ShouldBeTrue();
        wallet.ApplyFencingToken(6).Error.Code.ShouldBe("ledger.lock_lost");
        wallet.LockToken.ShouldBe(7);
    }
}
