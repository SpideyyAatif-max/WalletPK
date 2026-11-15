using Microsoft.EntityFrameworkCore;
using WalletPK.Payments.Application.BankTransfers;
using WalletPK.Persistence.Inbox;
using WalletPK.Persistence.Outbox;
using WalletPK.Sagas;

namespace WalletPK.Payments.Infrastructure.Persistence;

/// <summary>
/// The Payments database (walletpk_payments). A transfer lives in its Saga row: state, data and timeline
/// (saga_instances, saga_steps). Commands to the Ledger and status events leave through the outbox;
/// the Ledger's replies come in through the inbox.
/// </summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxMessages();
        modelBuilder.AddInboxMessages();
        modelBuilder.AddSagas();
    }
}

/// <summary>A customer's transfers, newest first (served by ix_saga_instances_bank_transfer_user).</summary>
internal sealed class BankTransferQueries(PaymentsDbContext db) : IBankTransferQueries
{
    public async Task<IReadOnlyList<SagaSnapshot<BankTransferData>>> ListAsync(Guid userId, int limit, DateTimeOffset? before, CancellationToken cancellationToken)
    {
        var user = userId.ToString();
        var sagaType = BankTransferSaga.SagaName;
        var query = before is { } cursor
            ? db.Set<SagaInstance>().FromSql($"""
                SELECT * FROM saga_instances
                 WHERE saga_type = {sagaType} AND data->>'userId' = {user} AND created_at < {cursor}
                 ORDER BY created_at DESC
                 LIMIT {limit}
                """)
            : db.Set<SagaInstance>().FromSql($"""
                SELECT * FROM saga_instances
                 WHERE saga_type = {sagaType} AND data->>'userId' = {user}
                 ORDER BY created_at DESC
                 LIMIT {limit}
                """);

        var rows = await query.AsNoTracking().ToListAsync(cancellationToken);
        return [.. rows.Select(row => SagaReader<PaymentsDbContext>.ToSnapshot<BankTransferData>(row, []))];
    }
}
