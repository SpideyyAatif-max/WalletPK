using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Concurrency;
using WalletPK.Ledger.Application.Events;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Domain;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;
using LedgerContracts = WalletPK.Contracts.Ledger;

namespace WalletPK.Ledger.UnitTests.Application;

public class PostingServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private readonly FakeLedgerStore _store = new();
    private readonly Account _ali;
    private readonly Account _sara;

    public PostingServiceTests()
    {
        foreach (var definition in ChartOfAccounts.SystemAccounts)
        {
            for (var shard = 0; shard < definition.Shards; shard++)
            {
                _store.Accounts.Add(Account.OpenSystemShard(definition, shard, _now));
            }
        }

        _ali = Account.OpenWallet(UserId.New(), AccountType.CustomerWallet, "Ali", _now);
        _sara = Account.OpenWallet(UserId.New(), AccountType.CustomerWallet, "Sara", _now);
        _store.Accounts.Add(_ali);
        _store.Accounts.Add(_sara);
    }

    private readonly FakeLocks _locks = new();

    private PostingService Service() => new(_store, _store, _store, new WalletConcurrency(_locks), new FixedTime(_now));

    private static PostJournalCommand CashIn(Account wallet, decimal rupees, TransactionId? id = null) =>
        new(id ?? TransactionId.New(), JournalType.CashIn, "Cash-in", "BANK-REF-1",
        [
            PostingLine.Debit(AccountRef.System(ChartOfAccounts.BankFloat), Money.FromRupees(rupees)),
            PostingLine.Credit(AccountRef.Account(wallet.Id), Money.FromRupees(rupees)),
        ]);

    [Fact]
    public async Task Posting_resolves_the_system_shard_saves_once_and_reports_balances()
    {
        var id = TransactionId.New();

        var result = await Service().PostAsync(CashIn(_ali, 1_000, id), Cancel);

        var bankShard = ChartOfAccounts.ShardIdFor(ChartOfAccounts.BankFloat, id);
        result.Value.AlreadyPosted.ShouldBeFalse();
        result.Value.Amount.ShouldBe(Money.FromRupees(1_000));
        result.Value.Entries.Select(entry => entry.AccountId).ShouldBe(new[] { bankShard, _ali.Id });
        result.Value.Entries[1].BalanceAfter.ShouldBe(Money.FromRupees(1_000));
        _store.Journals.ShouldHaveSingleItem().Id.ShouldBe(id);
        _store.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task Posting_the_same_transaction_again_returns_the_original_and_moves_nothing()
    {
        var id = TransactionId.New();
        await Service().PostAsync(CashIn(_ali, 1_000, id), Cancel);

        var retry = await Service().PostAsync(CashIn(_ali, 1_000, id), Cancel);

        retry.Value.AlreadyPosted.ShouldBeTrue();
        _ali.Balance.ShouldBe(100_000);
        _store.Journals.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_p2p_transfer_with_fee_and_tax_balances()
    {
        await Service().PostAsync(CashIn(_ali, 1_000), Cancel);

        var transfer = await Service().PostAsync(
            new PostJournalCommand(TransactionId.New(), JournalType.P2PTransfer, "Ali to Sara", null,
            [
                PostingLine.Debit(AccountRef.Account(_ali.Id), Money.FromRupees(511.60m)),
                PostingLine.Credit(AccountRef.Account(_sara.Id), Money.FromRupees(500)),
                PostingLine.Credit(AccountRef.System(ChartOfAccounts.FeeIncome), Money.FromRupees(10)),
                PostingLine.Credit(AccountRef.System(ChartOfAccounts.TaxPayable), Money.FromRupees(1.60m)),
            ]),
            Cancel);

        transfer.IsSuccess.ShouldBeTrue();
        _ali.Balance.ShouldBe(48_840);
        _sara.Balance.ShouldBe(50_000);
    }

    [Fact]
    public async Task Unknown_accounts_and_overdrafts_are_refused_without_saving()
    {
        var unknown = await Service().PostAsync(CashIn(Account.OpenWallet(UserId.New(), AccountType.CustomerWallet, "Ghost", _now), 10), Cancel);
        var overdraft = await Service().PostAsync(
            new PostJournalCommand(TransactionId.New(), JournalType.P2PTransfer, "Too much", null,
            [
                PostingLine.Debit(AccountRef.Account(_ali.Id), Money.FromRupees(1)),
                PostingLine.Credit(AccountRef.Account(_sara.Id), Money.FromRupees(1)),
            ]),
            Cancel);

        unknown.Error.Code.ShouldBe("ledger.account_not_found");
        overdraft.Error.Code.ShouldBe("ledger.insufficient_funds");
        _store.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Non_pkr_amounts_are_refused()
    {
        var result = await Service().PostAsync(
            new PostJournalCommand(TransactionId.New(), JournalType.CashIn, "USD", null,
            [
                PostingLine.Debit(AccountRef.System(ChartOfAccounts.BankFloat), Money.FromMajorUnits(5, Currency.Usd)),
                PostingLine.Credit(AccountRef.Account(_ali.Id), Money.FromMajorUnits(5, Currency.Usd)),
            ]),
            Cancel);

        result.Error.ShouldBe(LedgerErrors.CurrencyMismatch);
    }

    [Fact]
    public async Task A_lost_concurrency_race_is_retried_and_succeeds_on_fresh_data()
    {
        _store.ConflictsBeforeSuccess = 2;

        var result = await Service().PostAsync(CashIn(_ali, 10), Cancel);

        result.IsSuccess.ShouldBeTrue();
        _store.SaveAttempts.ShouldBe(3);
        _store.Journals.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Repeated_concurrency_conflicts_give_up_after_three_attempts()
    {
        _store.ConflictsBeforeSuccess = 10;

        var result = await Service().PostAsync(CashIn(_ali, 10), Cancel);

        result.Error.ShouldBe(PostingService.ConcurrentUpdate);
        _store.SaveAttempts.ShouldBe(WalletConcurrency.MaxAttempts);
    }

    [Fact]
    public async Task Only_wallets_are_locked_never_system_account_shards()
    {
        await Service().PostAsync(CashIn(_ali, 10), Cancel);

        _locks.LastRequested.ShouldBe(new[] { _ali.Id });
        _locks.Released.ShouldBeTrue();
    }

    [Fact]
    public async Task A_busy_wallet_is_reported_without_posting()
    {
        _locks.Busy = true;

        var result = await Service().PostAsync(CashIn(_ali, 10), Cancel);

        result.Error.ShouldBe(WalletConcurrency.WalletBusy);
        _store.SaveAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task A_writer_whose_lock_expired_is_refused_by_the_fencing_token()
    {
        _ali.ApplyFencingToken(50).IsSuccess.ShouldBeTrue(); // a newer lock holder already wrote with token 50
        _locks.NextToken = 49;                                 // our lock is older

        var result = await Service().PostAsync(CashIn(_ali, 10), Cancel);

        result.Error.Code.ShouldBe("ledger.lock_lost");
        _ali.Balance.ShouldBe(0);
    }

    [Fact]
    public void A_posted_journal_becomes_the_public_journal_posted_event()
    {
        var bank = Account.OpenSystemShard(ChartOfAccounts.BankFloat, 1, _now);
        var journal = JournalTransaction.Post(
            TransactionId.New(), JournalType.CashIn, "Cash-in", "REF-9",
            [new JournalLine(bank, EntryDirection.Debit, 250_00), new JournalLine(_ali, EntryDirection.Credit, 250_00)],
            _now).Value;
        var domainEvent = journal.DomainEvents.Single();

        var published = LedgerIntegrationEvents.FromDomainEvent(domainEvent).ShouldBeOfType<LedgerContracts.JournalPosted>();

        published.EventId.ShouldBe(domainEvent.EventId);
        published.TransactionId.ShouldBe(journal.Id.Value);
        published.GetPartitionKey().ShouldBe(journal.Id.ToString());
        published.JournalType.ShouldBe("CashIn");
        published.AmountPaisa.ShouldBe(250_00);
        published.Entries.Select(entry => (entry.Direction, entry.BalanceAfterPaisa))
            .ShouldBe(new[] { ("Debit", 250_00L), ("Credit", 250_00L) });
    }

    /// <summary>Hands out locks with a chosen fencing token, or reports the wallet busy.</summary>
    private sealed class FakeLocks : IAccountLockService
    {
        public bool Busy { get; set; }

        public long NextToken { get; set; } = 1;

        public IReadOnlyCollection<AccountId> LastRequested { get; private set; } = [];

        public bool Released { get; private set; }

        public Task<IAccountLockLease?> TryAcquireAsync(IReadOnlyCollection<AccountId> accountIds, CancellationToken cancellationToken)
        {
            LastRequested = accountIds;
            return Task.FromResult<IAccountLockLease?>(Busy ? null : new Lease(this, accountIds.ToDictionary(id => id, _ => NextToken)));
        }

        private sealed class Lease(FakeLocks owner, Dictionary<AccountId, long> tokens) : IAccountLockLease
        {
            public IReadOnlyDictionary<AccountId, long> FencingTokens => tokens;

            public ValueTask DisposeAsync()
            {
                owner.Released = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>In-memory accounts, journals and unit of work.</summary>
    private sealed class FakeLedgerStore : IAccountRepository, IJournalRepository, ILedgerUnitOfWork
    {
        private readonly List<JournalTransaction> _pending = [];

        public List<Account> Accounts { get; } = [];

        public List<JournalTransaction> Journals { get; } = [];

        public int SaveCount { get; private set; }

        public int ConflictsBeforeSuccess { get; set; }

        public int SaveAttempts { get; private set; }

        public Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken) =>
            Task.FromResult(Accounts.FirstOrDefault(account => account.Id == id));

        public Task<IReadOnlyList<Account>> GetManyAsync(IReadOnlyCollection<AccountId> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Account>>([.. Accounts.Where(account => ids.Contains(account.Id))]);

        public void Add(Account account) => Accounts.Add(account);

        public Task<JournalTransaction?> GetAsync(TransactionId id, CancellationToken cancellationToken) =>
            Task.FromResult(Journals.FirstOrDefault(journal => journal.Id == id));

        public void Add(JournalTransaction journal) => _pending.Add(journal);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveAttempts++;
            if (SaveAttempts <= ConflictsBeforeSuccess)
            {
                _pending.Clear();
                throw new ConcurrencyConflictException("xmin changed");
            }

            Journals.AddRange(_pending);
            _pending.Clear();
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
