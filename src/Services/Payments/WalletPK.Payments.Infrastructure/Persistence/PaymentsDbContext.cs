using Microsoft.EntityFrameworkCore;
using WalletPK.Payments.Application.BankTransfers;
using WalletPK.Payments.Application.Wallets;
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

/// <summary>Lists over saga_instances; every field has a partial expression index (see the migrations).</summary>
internal sealed class SagaQueries(PaymentsDbContext db) : ISagaQueries
{
    public async Task<IReadOnlyList<SagaSnapshot<TData>>> ListAsync<TData>(
        string sagaType,
        SagaListField field,
        string value,
        int limit,
        DateTimeOffset? before,
        CancellationToken cancellationToken)
        where TData : class
    {
        // The JSON key is one of a fixed set (never caller text), so it is written into the SQL, where the
        // planner can match it to the expression index; everything else is a parameter.
        var key = field switch
        {
            SagaListField.UserId => "userId",
            SagaListField.SenderUserId => "senderUserId",
            SagaListField.RequesterUserId => "requesterUserId",
            SagaListField.PayerWalletNumber => "payerWalletNumber",
            SagaListField.SplitId => "splitId",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var sql = $"SELECT * FROM saga_instances WHERE saga_type = {{0}} AND data->>'{key}' = {{1}}"
            + (before is null ? "" : " AND created_at < {3}")
            + " ORDER BY created_at DESC LIMIT {2}";
        object[] parameters = before is { } cursor ? [sagaType, value, limit, cursor] : [sagaType, value, limit];

        var rows = await db.Set<SagaInstance>().FromSqlRaw(sql, parameters).AsNoTracking().ToListAsync(cancellationToken);
        return [.. rows.Select(row => SagaReader<PaymentsDbContext>.ToSnapshot<TData>(row, []))];
    }

    public async Task<IReadOnlyList<Guid>> SplitIdsAsync(Guid requesterUserId, int limit, CancellationToken cancellationToken)
    {
        var user = requesterUserId.ToString();
        var sagaType = MoneyRequestSaga.SagaName;
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT (data->>'splitId')::uuid AS "Value"
              FROM saga_instances
             WHERE saga_type = {sagaType} AND data->>'requesterUserId' = {user} AND data->>'splitId' IS NOT NULL
             GROUP BY data->>'splitId'
             ORDER BY MIN(created_at) DESC
             LIMIT {limit}
            """).ToListAsync(cancellationToken);
        return ids;
    }
}
