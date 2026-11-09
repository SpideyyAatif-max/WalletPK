using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Holds;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Audit;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>M3.10: every money-relevant change leaves an audit row, chained by SHA-256, and tampering is found.</summary>
[Collection(LedgerCollection.Name)]
public class AuditChainTests(LedgerApiFactory factory)
{
    private Task<List<AuditRecord>> AuditRowsAsync(string subjectId) =>
        factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().AuditLog.AsNoTracking()
            .Where(record => record.SubjectId == subjectId)
            .OrderBy(record => record.Id)
            .ToListAsync(Cancel));

    private Task<AuditChainReport> VerifyAsync() =>
        factory.InScopeAsync(services => services.GetRequiredService<AuditChainVerifier>().VerifyAsync(Cancel));

    /// <summary>Seals everything and returns the head row, which tampering tests then work on.</summary>
    private async Task<AuditRecord> SealedHeadAsync()
    {
        await factory.SealAuditAsync();
        return await factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().AuditLog.AsNoTracking()
            .Where(record => record.ChainSequence != null)
            .OrderByDescending(record => record.ChainSequence)
            .FirstAsync(Cancel));
    }

    [Fact]
    public async Task A_transfer_is_audited_with_who_did_it_in_the_same_transaction()
    {
        var sender = await factory.CustomerAsync(rupees: 2_000);
        var recipient = await factory.CustomerAsync();
        var key = NewKey();

        using var response = await sender.SendAsync(recipient.Phone, 150_000, key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var transactionId = WalletService.TransactionIdFor(sender.Id, key);
        var row = (await AuditRowsAsync(transactionId.ToString())).ShouldHaveSingleItem();
        row.EventType.ShouldBe("journal.posted");
        row.SubjectType.ShouldBe("journal");
        row.Actor.ShouldBe($"user:{sender.Id}");
        row.CorrelationId.ShouldNotBeNullOrEmpty();
        row.Payload.ShouldContain("\"type\":\"P2PTransfer\"");
        row.Payload.ShouldContain("\"amount\":150870");
        row.IsSealed.ShouldBeFalse();

        // Opening the wallet was audited too.
        (await AuditRowsAsync(sender.WalletId.ToString())).ShouldContain(record => record.EventType == "account.opened");
    }

    [Fact]
    public async Task A_refused_posting_leaves_no_audit_row()
    {
        var wallet = await factory.CustomerAsync(rupees: 10);
        var transactionId = TransactionId.New();

        var result = await factory.InScopeAsync(services => services.GetRequiredService<PostingService>().PostAsync(
            new PostJournalCommand(transactionId, JournalType.Adjustment, "Too much", null,
            [
                PostingLine.Debit(AccountRef.Account(wallet.WalletId), Money.FromRupees(11)),
                PostingLine.Credit(AccountRef.System(ChartOfAccounts.Suspense), Money.FromRupees(11)),
            ]),
            Cancel));

        result.IsFailure.ShouldBeTrue();
        (await AuditRowsAsync(transactionId.ToString())).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_hold_is_audited_when_placed_and_when_released()
    {
        var wallet = await factory.CustomerAsync(rupees: 500);

        var placed = await factory.InScopeAsync(services => services.GetRequiredService<HoldService>().PlaceAsync(
            new PlaceHoldCommand(wallet.WalletId, Money.FromRupees(200), "Bill payment", "BILL-1", TimeSpan.FromMinutes(10)), Cancel));
        await factory.InScopeAsync(services => services.GetRequiredService<HoldService>().ReleaseAsync(placed.Value.HoldId, Cancel));

        var rows = await AuditRowsAsync(placed.Value.HoldId.ToString());
        rows.Select(row => row.EventType).ShouldBe(["hold.placed", "hold.released"]);
        rows.ShouldAllBe(row => row.Actor == LedgerAuditInterceptor.SystemActor);
    }

    [Fact]
    public async Task Sealing_links_every_row_to_the_one_before_it()
    {
        await factory.CustomerAsync(rupees: 100);

        await factory.SealAuditAsync();
        var report = await VerifyAsync();

        report.Intact.ShouldBeTrue(report.Problem);
        report.PendingCount.ShouldBe(0);
        report.SealedCount.ShouldBe(report.HeadSequence);

        var chain = await factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().AuditLog.AsNoTracking()
            .OrderBy(record => record.ChainSequence)
            .ToListAsync(Cancel));
        chain[0].PreviousHash.ShouldBe(AuditChain.GenesisHash);
        for (var i = 1; i < chain.Count; i++)
        {
            chain[i].ChainSequence.ShouldBe(i + 1);
            chain[i].PreviousHash.ShouldBe(chain[i - 1].Hash);
        }

        report.HeadHash.ShouldBe(chain[^1].Hash);
    }

    [Fact]
    public async Task Parallel_sealers_never_fork_the_chain()
    {
        for (var i = 0; i < 10; i++)
        {
            await factory.CustomerAsync(rupees: 1);
        }

        // Ten sealers at once: the advisory lock lets one work at a time; the others seal nothing or the rest.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => factory.InScopeAsync(services =>
            services.GetRequiredService<AuditSealer>().SealPendingAsync(3, Cancel))));
        await factory.SealAuditAsync();

        var report = await VerifyAsync();
        report.Intact.ShouldBeTrue(report.Problem);
        report.PendingCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("UPDATE audit_log SET payload = '{}' WHERE id = @id")]
    [InlineData("UPDATE audit_log SET actor = 'someone-else' WHERE id = @id")]
    [InlineData("UPDATE audit_log SET hash = repeat('a', 64) WHERE id = @id")]
    [InlineData("DELETE FROM audit_log WHERE id = @id")]
    public async Task The_database_refuses_to_change_or_delete_a_sealed_row(string sql)
    {
        await factory.CustomerAsync();
        var head = await SealedHeadAsync();

        var exception = await Should.ThrowAsync<PostgresException>(() => factory.ExecuteSqlAsync(sql, ("id", head.Id)));

        exception.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task The_database_refuses_to_change_an_unsealed_row_too()
    {
        var customer = await factory.CustomerAsync();
        var unsealed = (await AuditRowsAsync(customer.WalletId.ToString())).First(row => !row.IsSealed);

        var exception = await Should.ThrowAsync<PostgresException>(() =>
            factory.ExecuteSqlAsync("UPDATE audit_log SET payload = '{}' WHERE id = @id", ("id", unsealed.Id)));

        exception.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task The_audit_log_cannot_be_truncated()
    {
        var exception = await Should.ThrowAsync<PostgresException>(() => factory.ExecuteSqlAsync("TRUNCATE audit_log"));

        exception.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task Editing_a_sealed_row_behind_the_guards_back_is_detected_at_that_row()
    {
        await factory.CustomerAsync(rupees: 50);
        await factory.CustomerAsync(rupees: 50);
        var head = await SealedHeadAsync();
        var victim = (await factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().AuditLog.AsNoTracking()
            .SingleAsync(record => record.ChainSequence == head.ChainSequence - 2, Cancel)));

        // An attacker with database owner rights switches the guard off and rewrites history.
        await factory.ExecuteSqlAsync(
            "ALTER TABLE audit_log DISABLE TRIGGER trg_audit_log_guard; UPDATE audit_log SET payload = @payload WHERE id = @id; ALTER TABLE audit_log ENABLE TRIGGER trg_audit_log_guard;",
            ("payload", victim.Payload + " "), ("id", victim.Id));
        try
        {
            var report = await VerifyAsync();

            report.Intact.ShouldBeFalse();
            report.FirstBrokenSequence.ShouldBe(victim.ChainSequence);
            report.Problem.ShouldNotBeNull().ShouldContain("modified after it was sealed");
        }
        finally
        {
            await factory.ExecuteSqlAsync(
                "ALTER TABLE audit_log DISABLE TRIGGER trg_audit_log_guard; UPDATE audit_log SET payload = @payload WHERE id = @id; ALTER TABLE audit_log ENABLE TRIGGER trg_audit_log_guard;",
                ("payload", victim.Payload), ("id", victim.Id));
        }

        (await VerifyAsync()).Intact.ShouldBeTrue();
    }

    [Fact]
    public async Task Removing_a_sealed_row_is_detected_as_a_gap()
    {
        await factory.CustomerAsync(rupees: 50);
        await factory.CustomerAsync(rupees: 50);
        var head = await SealedHeadAsync();
        var removedSequence = head.ChainSequence!.Value - 1;

        // Moving a row out of its chain position has the same effect as deleting it, and is easy to undo.
        await factory.ExecuteSqlAsync(
            "ALTER TABLE audit_log DISABLE TRIGGER trg_audit_log_guard; UPDATE audit_log SET chain_sequence = chain_sequence + 1000000000 WHERE chain_sequence = @sequence;",
            ("sequence", removedSequence));
        try
        {
            var report = await VerifyAsync();

            report.Intact.ShouldBeFalse();
            report.FirstBrokenSequence.ShouldBe(removedSequence);
            report.Problem.ShouldNotBeNull().ShouldContain("missing");
        }
        finally
        {
            await factory.ExecuteSqlAsync(
                "UPDATE audit_log SET chain_sequence = @sequence WHERE chain_sequence = @sequence + 1000000000; ALTER TABLE audit_log ENABLE TRIGGER trg_audit_log_guard;",
                ("sequence", removedSequence));
        }

        (await VerifyAsync()).Intact.ShouldBeTrue();
    }
}
