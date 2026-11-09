using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Ledger.IntegrationTests.Infrastructure;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>
/// These tests go around the application on purpose, writing raw SQL as a buggy service or a person
/// with database access might, to prove PostgreSQL itself refuses to corrupt the ledger (M3.2).
/// </summary>
[Collection(LedgerCollection.Name)]
public class DatabaseGuardTests(LedgerApiFactory factory)
{
    private const string CheckViolation = "23514";
    private const string RestrictViolation = "23001";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private async Task<NpgsqlConnection> OpenAsync()
    {
        using var client = factory.CreateClient(); // ensures the service started and migrated the database
        var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Cancel);
        return connection;
    }

    private static NpgsqlCommand Sql(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    /// <summary>A wallet with Rs 100 and one posted cash-in journal, created the proper way.</summary>
    private async Task<(AccountId Wallet, TransactionId Journal)> FundedWalletAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var wallet = Account.OpenWallet(UserId.New(), AccountType.CustomerWallet, "Guarded", DateTimeOffset.UtcNow);
        db.Accounts.Add(wallet);
        await db.SaveChangesAsync(Cancel);

        var id = TransactionId.New();
        var posted = await scope.ServiceProvider.GetRequiredService<PostingService>().PostAsync(
            new PostJournalCommand(id, JournalType.CashIn, "Funding", null,
            [
                PostingLine.Debit(AccountRef.System(ChartOfAccounts.BankFloat), Money.FromRupees(100)),
                PostingLine.Credit(AccountRef.Account(wallet.Id), Money.FromRupees(100)),
            ]),
            Cancel);
        posted.IsSuccess.ShouldBeTrue();
        return (wallet.Id, id);
    }

    [Fact]
    public async Task An_unbalanced_journal_is_rejected_at_commit()
    {
        await using var connection = await OpenAsync();
        var id = Guid.CreateVersion7();
        var suspense = ChartOfAccounts.IdFor("SUSPENSE", 0).Value;

        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using (var header = Sql(connection,
            "INSERT INTO journal_transactions (id, type, description, amount, currency, posted_at) VALUES (@id, 'Adjustment', 'one-sided', 500, 'PKR', now())",
            ("id", id)))
        {
            await header.ExecuteNonQueryAsync(Cancel);
        }

        await using (var entry = Sql(connection,
            "INSERT INTO journal_entries (transaction_id, line_number, account_id, direction, amount, balance_after, posted_at) VALUES (@id, 1, @account, 'Debit', 500, 0, now())",
            ("id", id), ("account", suspense)))
        {
            await entry.ExecuteNonQueryAsync(Cancel); // accepted for now: the check is deferred to COMMIT
        }

        var error = await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync(Cancel));
        error.SqlState.ShouldBe(CheckViolation);
        error.MessageText.ShouldContain("Unbalanced journal");
    }

    [Fact]
    public async Task A_journal_header_without_entries_is_rejected_at_commit()
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using (var header = Sql(connection,
            "INSERT INTO journal_transactions (id, type, description, amount, currency, posted_at) VALUES (@id, 'Adjustment', 'empty', 500, 'PKR', now())",
            ("id", Guid.CreateVersion7())))
        {
            await header.ExecuteNonQueryAsync(Cancel);
        }

        var error = await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync(Cancel));
        error.SqlState.ShouldBe(CheckViolation);
    }

    [Theory]
    [InlineData("UPDATE journal_entries SET amount = amount + 1 WHERE transaction_id = @id")]
    [InlineData("DELETE FROM journal_entries WHERE transaction_id = @id")]
    [InlineData("UPDATE journal_transactions SET description = 'rewritten' WHERE id = @id")]
    [InlineData("DELETE FROM journal_transactions WHERE id = @id")]
    public async Task Posted_history_can_never_be_changed_or_deleted(string sql)
    {
        var (_, journal) = await FundedWalletAsync();
        await using var connection = await OpenAsync();
        await using var command = Sql(connection, sql, ("id", journal.Value));

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Cancel));

        error.SqlState.ShouldBe(RestrictViolation);
        error.MessageText.ShouldContain("append-only");
    }

    [Fact]
    public async Task History_cannot_be_truncated()
    {
        await using var connection = await OpenAsync();
        await using var command = Sql(connection, "TRUNCATE journal_entries");

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Cancel));

        error.SqlState.ShouldBe(RestrictViolation);
    }

    [Fact]
    public async Task A_wallet_balance_can_never_be_set_below_zero()
    {
        var (wallet, _) = await FundedWalletAsync();
        await using var connection = await OpenAsync();
        await using var command = Sql(connection, "UPDATE accounts SET balance = -1 WHERE id = @id", ("id", wallet.Value));

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Cancel));

        error.SqlState.ShouldBe(CheckViolation);
        error.ConstraintName.ShouldBe("ck_accounts_available_non_negative");
    }

    [Fact]
    public async Task Entry_amounts_must_be_positive()
    {
        var (wallet, _) = await FundedWalletAsync();
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using var command = Sql(connection,
            "INSERT INTO journal_entries (transaction_id, line_number, account_id, direction, amount, balance_after, posted_at) VALUES (@id, 1, @account, 'Credit', -5, 0, now())",
            ("id", Guid.CreateVersion7()), ("account", wallet.Value));

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Cancel));

        error.SqlState.ShouldBe(CheckViolation);
    }
}
