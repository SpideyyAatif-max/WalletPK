using FsCheck;
using FsCheck.Fluent;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Fees;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.UnitTests.Domain;

/// <summary>
/// M3.12 property-based tests: FsCheck generates hundreds of random sequences of postings and holds,
/// and after every single step the ledger's laws must still hold. When a law breaks, FsCheck shrinks
/// the sequence to the smallest one that still breaks it and prints it (with the seed to replay).
/// </summary>
public class LedgerPropertyTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static readonly Config _config = Config.QuickThrowOnFailure.WithMaxTest(300);

    /// <summary>One journal: <see cref="From"/> is debited, <see cref="To"/> credited; an optional fee line goes to FEE_INCOME.</summary>
    public sealed record Move(int From, int To, long Amount, long Fee)
    {
        public override string ToString() => $"{From}->{To} {Amount} (+fee {Fee})";
    }

    /// <summary>Accounts: 0 = bank float (debit-normal, may go negative), 1-4 = wallets, 5 = fee income.</summary>
    private static Account[] OpenBooks() =>
    [
        Account.OpenSystemShard(ChartOfAccounts.BankFloat, 0, _now),
        .. Enumerable.Range(1, 4).Select(i => Account.OpenWallet(UserId.New(), AccountType.CustomerWallet, $"Wallet {i}", _now)),
        Account.OpenSystemShard(ChartOfAccounts.FeeIncome, 0, _now),
    ];

    private static readonly Gen<Move> _move =
        from debited in Gen.Choose(0, 4)
        from credited in Gen.Choose(0, 4)
        from amount in Gen.Choose(1, 500_000)
        from withFee in Gen.Elements(false, true)
        from fee in Gen.Choose(1, 5_000)
        select new Move(debited, credited, amount, withFee ? fee : 0);

    /// <summary>Credit-normal minus debit-normal balances: zero means no money appeared or vanished.</summary>
    private static long TrialBalance(IEnumerable<Account> accounts) =>
        accounts.Sum(account => account.NormalBalance == NormalBalance.Credit ? account.Balance : -account.Balance);

    [Fact]
    public void Any_sequence_of_postings_conserves_money_and_never_overdraws_a_wallet()
    {
        var property = Prop.ForAll(Arb.From(_move.ListOf()), moves =>
        {
            var books = OpenBooks();
            foreach (var move in moves)
            {
                var before = books.Select(account => account.Balance).ToArray();
                List<JournalLine> lines =
                [
                    new(books[move.From], EntryDirection.Debit, move.Amount + move.Fee),
                    new(books[move.To], EntryDirection.Credit, move.Amount),
                ];
                if (move.Fee > 0)
                {
                    lines.Add(new JournalLine(books[5], EntryDirection.Credit, move.Fee));
                }

                var posted = JournalTransaction.Post(TransactionId.New(), JournalType.Adjustment, "property", null, lines, _now);

                if (posted.IsFailure)
                {
                    // All or nothing: a refused journal changes no balance at all.
                    posted.Error.Code.ShouldBe("ledger.insufficient_funds");
                    books.Select(account => account.Balance).ShouldBe(before);
                }
                else
                {
                    var journal = posted.Value;
                    journal.Entries.Where(entry => entry.Direction == EntryDirection.Debit).Sum(entry => entry.Amount)
                        .ShouldBe(journal.Entries.Where(entry => entry.Direction == EntryDirection.Credit).Sum(entry => entry.Amount));

                    // Each account's last entry in the journal shows its balance after the journal.
                    foreach (var last in journal.Entries.GroupBy(entry => entry.AccountId).Select(group => group.Last()))
                    {
                        last.BalanceAfter.ShouldBe(books.Single(account => account.Id == last.AccountId).Balance);
                    }
                }

                TrialBalance(books).ShouldBe(0);
                books.Where(account => account.IsWallet).ShouldAllBe(account => account.Balance >= 0 && account.Available >= 0);
            }
        });

        Check.One(_config, property);
    }

    /// <summary>A hold step: place a hold of <see cref="Amount"/>, or capture / release hold number <see cref="Index"/>.</summary>
    public sealed record HoldStep(string Kind, long Amount, int Index, int CapturePercent)
    {
        public override string ToString() => Kind == "place" ? $"place {Amount}" : $"{Kind} #{Index} ({CapturePercent}%)";
    }

    private static readonly Gen<HoldStep> _holdStep =
        from kind in Gen.Elements("place", "place", "capture", "release")
        from amount in Gen.Choose(1, 300_000)
        from index in Gen.Choose(0, 9)
        from percent in Gen.Choose(1, 100)
        select new HoldStep(kind, amount, index, percent);

    [Fact]
    public void Holds_always_match_the_held_amount_and_captures_always_fit()
    {
        var property = Prop.ForAll(Arb.From(Gen.Choose(0, 1_000_000)), Arb.From(_holdStep.ListOf()), (opening, steps) =>
        {
            var books = OpenBooks();
            var wallet = books[1];
            if (opening > 0)
            {
                JournalTransaction.Post(TransactionId.New(), JournalType.CashIn, "cash-in", null,
                    [new(books[0], EntryDirection.Debit, opening), new(wallet, EntryDirection.Credit, opening)], _now).IsSuccess.ShouldBeTrue();
            }

            var holds = new List<Hold>();
            foreach (var step in steps)
            {
                var balanceBefore = wallet.Balance;
                switch (step.Kind)
                {
                    case "place":
                        var placed = Hold.Place(wallet, step.Amount, "property", null, TimeSpan.FromHours(1), _now);
                        if (placed.IsSuccess)
                        {
                            holds.Add(placed.Value);
                        }
                        else
                        {
                            placed.Error.Code.ShouldBe("ledger.insufficient_funds");
                        }

                        wallet.Balance.ShouldBe(balanceBefore); // a hold never moves money, it only reserves it
                        break;

                    case "release" when step.Index < holds.Count:
                        var hold = holds[step.Index];
                        var wasActive = hold.Status == HoldStatus.Active;
                        var released = hold.Release(wallet, _now);
                        released.IsSuccess.ShouldBe(wasActive); // a hold is settled once, never twice
                        wallet.Balance.ShouldBe(balanceBefore);
                        break;

                    case "capture" when step.Index < holds.Count && holds[step.Index].Status == HoldStatus.Active:
                        var target = holds[step.Index];
                        var amount = Math.Max(1, target.Amount * step.CapturePercent / 100);
                        target.Capture(wallet, amount, TransactionId.New(), _now).IsSuccess.ShouldBeTrue();

                        // The money was reserved, so the capture journal can never bounce.
                        JournalTransaction.Post(TransactionId.New(), JournalType.BillPayment, "capture", null,
                            [new(wallet, EntryDirection.Debit, amount), new(books[5], EntryDirection.Credit, amount)], _now)
                            .IsSuccess.ShouldBeTrue();
                        break;
                }

                wallet.Held.ShouldBe(holds.Where(item => item.Status == HoldStatus.Active).Sum(item => item.Amount));
                wallet.Available.ShouldBeGreaterThanOrEqualTo(0);
                TrialBalance(books).ShouldBe(0);
            }
        });

        Check.One(_config, property);
    }

    [Fact]
    public void Fees_stay_within_their_band_and_tax_is_always_16_percent_of_the_fee()
    {
        var engine = new FeeEngine(new FeeOptions
        {
            Schedules =
            {
                ["P2PTransfer"] = [new FeeBand { UpToRupees = 1_000 }, new FeeBand { Percent = 0.5m, MinRupees = 5, MaxRupees = 50 }],
            },
        });

        var property = Prop.ForAll(Arb.From(Gen.Choose(1, int.MaxValue)), paisa =>
        {
            var amount = Money.FromPaisa(paisa);
            var quote = engine.Quote(JournalType.P2PTransfer, amount);

            if (paisa <= 100_000)
            {
                quote.IsFree.ShouldBeTrue();
            }
            else
            {
                quote.Fee.ShouldBeInRange(Money.FromRupees(5), Money.FromRupees(50));
                quote.Fee.ShouldBe(Money.Min(Money.Max(amount.Percent(0.5m), Money.FromRupees(5)), Money.FromRupees(50)));
            }

            quote.Tax.ShouldBe(quote.Fee.Percent(16));
            quote.TotalDebit.ShouldBe(amount + quote.Fee + quote.Tax);
        });

        Check.One(Config.QuickThrowOnFailure.WithMaxTest(1_000), property);
    }
}
