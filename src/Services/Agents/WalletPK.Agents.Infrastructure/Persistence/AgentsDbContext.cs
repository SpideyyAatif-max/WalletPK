using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Agents.Application;
using WalletPK.Agents.Domain;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Inbox;
using WalletPK.Persistence.Outbox;
using WalletPK.Sagas;

namespace WalletPK.Agents.Infrastructure.Persistence;

/// <summary>
/// The Agent Network database (walletpk_agents): cash-ins, cash-outs and float moves live in their Saga
/// rows (saga_instances, saga_steps); agents, cash-out requests and commission payouts in their own tables.
/// Ledger commands and our events leave through the outbox, the Ledger's replies come in through the inbox.
/// </summary>
public sealed class AgentsDbContext(DbContextOptions<AgentsDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxMessages();
        modelBuilder.AddInboxMessages();
        modelBuilder.AddSagas();
        modelBuilder.ApplyConfiguration(new AgentConfiguration());
        modelBuilder.ApplyConfiguration(new CashOutRequestConfiguration());
        modelBuilder.ApplyConfiguration(new CommissionPayoutConfiguration());
        modelBuilder.ApplyConfiguration(new PaidCommissionConfiguration());
    }
}

internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents");
        builder.HasKey(agent => agent.Id);
        builder.Property(agent => agent.Id).ValueGeneratedNever();
        builder.Property(agent => agent.Code).HasMaxLength(10).IsRequired();
        builder.Property(agent => agent.ShopName).HasMaxLength(AgentRules.MaxShopNameLength).IsRequired();
        builder.Property(agent => agent.City).HasMaxLength(AgentRules.MaxPlaceLength).IsRequired();
        builder.Property(agent => agent.Area).HasMaxLength(AgentRules.MaxPlaceLength);
        builder.Property(agent => agent.Status).HasMaxLength(20).IsRequired();
        builder.Property(agent => agent.StatusReason).HasMaxLength(200);
        builder.HasIndex(agent => agent.OwnerUserId).IsUnique();
        builder.HasIndex(agent => agent.Code).IsUnique();
        builder.HasIndex(agent => new { agent.Status, agent.City });
    }
}

internal sealed class CashOutRequestConfiguration : IEntityTypeConfiguration<CashOutRequest>
{
    public void Configure(EntityTypeBuilder<CashOutRequest> builder)
    {
        builder.ToTable("cash_out_requests");
        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id).ValueGeneratedNever();
        builder.Property(request => request.CustomerWalletNumber).HasMaxLength(13).IsRequired();
        builder.Property(request => request.CustomerDeviceId).HasMaxLength(100);
        builder.Property(request => request.CodeHash).HasMaxLength(64).IsRequired();
        builder.HasOne<Agent>().WithMany().HasForeignKey(request => request.AgentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(request => new { request.CustomerUserId, request.CreatedAt });
        builder.HasIndex(request => new { request.CustomerWalletNumber, request.AgentId });
    }
}

internal sealed class CommissionPayoutConfiguration : IEntityTypeConfiguration<CommissionPayout>
{
    public void Configure(EntityTypeBuilder<CommissionPayout> builder)
    {
        builder.ToTable("commission_payouts");
        builder.HasKey(payout => payout.Id);
        builder.Property(payout => payout.Id).ValueGeneratedNever();
        builder.Property(payout => payout.Status).HasMaxLength(16).IsRequired();
        builder.Property(payout => payout.FailureReason).HasMaxLength(200);
        builder.HasOne<Agent>().WithMany().HasForeignKey(payout => payout.AgentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(payout => new { payout.AgentId, payout.BusinessDate });
        builder.HasIndex(payout => payout.Status);
    }
}

internal sealed class PaidCommissionConfiguration : IEntityTypeConfiguration<PaidCommission>
{
    public void Configure(EntityTypeBuilder<PaidCommission> builder)
    {
        builder.ToTable("paid_commissions");
        builder.HasKey(paid => paid.TransactionId);
        builder.Property(paid => paid.TransactionId).ValueGeneratedNever();
        builder.HasOne<CommissionPayout>().WithMany().HasForeignKey(paid => paid.PayoutId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(paid => paid.PayoutId);
    }
}

internal sealed class AgentStore(AgentsDbContext db, JsonEventSerializer serializer, TimeProvider timeProvider) : IAgentStore
{
    public Task<Agent?> ByOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken) =>
        db.Set<Agent>().SingleOrDefaultAsync(agent => agent.OwnerUserId == ownerUserId, cancellationToken);

    public Task<Agent?> ByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Set<Agent>().SingleOrDefaultAsync(agent => agent.Id == id, cancellationToken);

    public Task<Agent?> ByCodeAsync(string code, CancellationToken cancellationToken) =>
        db.Set<Agent>().SingleOrDefaultAsync(agent => agent.Code == code, cancellationToken);

    public async Task<IReadOnlyList<Agent>> ActiveAsync(string? city, int limit, CancellationToken cancellationToken) =>
        await db.Set<Agent>().AsNoTracking()
            .Where(agent => agent.Status == AgentStatus.Active && (city == null || EF.Functions.ILike(agent.City, city)))
            .OrderBy(agent => agent.ShopName)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Agent>> WithStatusAsync(string status, int limit, CancellationToken cancellationToken) =>
        await db.Set<Agent>().Where(agent => agent.Status == status).OrderBy(agent => agent.CreatedAt).Take(limit).ToListAsync(cancellationToken);

    public void Add(Agent agent) => db.Set<Agent>().Add(agent);

    public void Add(CashOutRequest request) => db.Set<CashOutRequest>().Add(request);

    public Task<CashOutRequest?> RequestAsync(Guid id, CancellationToken cancellationToken) =>
        db.Set<CashOutRequest>().AsNoTracking().SingleOrDefaultAsync(request => request.Id == id, cancellationToken);

    public async Task<IReadOnlyList<CashOutRequest>> OpenRequestsAsync(Guid customerUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Set<CashOutRequest>()
            .Where(request => request.CustomerUserId == customerUserId && request.CancelledAt == null && request.TransactionId == null
                && request.ExpiresAt > now && request.FailedAttempts < CashOutCodes.MaxAttempts)
            .ToListAsync(cancellationToken);

    public Task<CashOutRequest?> LatestRequestAsync(Guid customerUserId, CancellationToken cancellationToken) =>
        db.Set<CashOutRequest>().AsNoTracking()
            .Where(request => request.CustomerUserId == customerUserId)
            .OrderByDescending(request => request.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<CashOutRequest?> OpenRequestAtAsync(string customerWalletNumber, Guid agentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var failed = "Failed";
        var maxAttempts = CashOutCodes.MaxAttempts;
        return await db.Set<CashOutRequest>()
            .FromSql($"""
                SELECT r.* FROM cash_out_requests AS r
                 WHERE r.customer_wallet_number = {customerWalletNumber} AND r.agent_id = {agentId}
                   AND r.cancelled_at IS NULL AND r.expires_at > {now} AND r.failed_attempts < {maxAttempts}
                   AND (r.transaction_id IS NULL
                        OR EXISTS (SELECT 1 FROM saga_instances AS s WHERE s.id = r.transaction_id AND s.state = {failed}))
                 ORDER BY r.created_at DESC
                 LIMIT 1
                """)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<int> CountFailedAttemptAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var attempts = await db.Database.SqlQuery<int>($"""
            UPDATE cash_out_requests SET failed_attempts = failed_attempts + 1 WHERE id = {requestId} RETURNING failed_attempts AS "Value"
            """).ToListAsync(cancellationToken);
        return attempts.Single();
    }

    public async Task<bool> TryClaimAsync(Guid requestId, Guid transactionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var failed = "Failed";
        var maxAttempts = CashOutCodes.MaxAttempts;
        var claimed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE cash_out_requests AS r SET transaction_id = {transactionId}
             WHERE r.id = {requestId} AND r.cancelled_at IS NULL AND r.expires_at > {now} AND r.failed_attempts < {maxAttempts}
               AND (r.transaction_id IS NULL
                    OR EXISTS (SELECT 1 FROM saga_instances AS s WHERE s.id = r.transaction_id AND s.state = {failed}))
            """,
            cancellationToken);
        return claimed == 1;
    }

    public async Task<IReadOnlyList<CommissionPayout>> PayoutsAsync(Guid agentId, int limit, CancellationToken cancellationToken) =>
        await db.Set<CommissionPayout>().AsNoTracking()
            .Where(payout => payout.AgentId == agentId)
            .OrderByDescending(payout => payout.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<UnpaidCommissions> UnpaidAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var rows = await CommissionQueries.UnpaidAsync(db, agentId, before: null, cancellationToken);
        return new UnpaidCommissions(rows.Count, rows.Sum(row => row.CommissionPaisa));
    }

    public void Publish(IntegrationEvent message, string? correlationId) =>
        db.AddToOutbox(serializer, message, timeProvider.GetUtcNow(), correlationId);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>A completed transaction whose commission no payout includes yet.</summary>
internal sealed record UnpaidCommission(Guid TransactionId, Guid AgentId, long CommissionPaisa);

internal static class CommissionQueries
{
    /// <summary>
    /// Completed transactions of <paramref name="agentId"/> with a commission that no payout includes yet,
    /// completed before <paramref name="before"/> (when given), oldest first.
    /// </summary>
    public static async Task<IReadOnlyList<UnpaidCommission>> UnpaidAsync(AgentsDbContext db, Guid agentId, DateTimeOffset? before, CancellationToken cancellationToken)
    {
        var sagaType = AgentTransactionSaga.SagaName;
        var completed = "Completed";
        var agent = agentId.ToString();
        var rows = await db.Set<SagaInstance>()
            .FromSql($"""
                SELECT s.* FROM saga_instances AS s
                 WHERE s.saga_type = {sagaType} AND s.state = {completed} AND s.data->>'agentId' = {agent}
                   AND (s.data->>'commissionPaisa')::bigint > 0
                   AND ({before}::timestamptz IS NULL OR s.completed_at < {before})
                   AND NOT EXISTS (SELECT 1 FROM paid_commissions AS p WHERE p.transaction_id = s.id)
                 ORDER BY s.completed_at
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return
        [
            .. rows.Select(row =>
            {
                var data = SagaReader<AgentsDbContext>.ReadData<AgentTransactionData>(row);
                return new UnpaidCommission(row.Id, data.AgentId, data.CommissionPaisa);
            }),
        ];
    }
}

/// <summary>Lists over saga_instances by a field of their data (partial expression indexes in the migrations).</summary>
internal sealed class SagaLists(AgentsDbContext db) : ISagaLists
{
    /// <summary>The fields lists are kept by (each has an index); nothing else reaches the SQL.</summary>
    private static readonly HashSet<string> Fields = ["agentId", "customerUserId"];

    public async Task<IReadOnlyList<SagaSnapshot<TData>>> ByFieldAsync<TData>(string sagaType, string field, string value, int limit, CancellationToken cancellationToken)
        where TData : class
    {
        if (!Fields.Contains(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field), field, "Not a listed field.");
        }

#pragma warning disable EF1002 // The field is one of the constants above; the values are parameters.
        var rows = await db.Set<SagaInstance>()
            .FromSqlRaw(
                $"SELECT * FROM saga_instances WHERE saga_type = {{0}} AND data->>'{field}' = {{1}} ORDER BY created_at DESC LIMIT {{2}}",
                sagaType,
                value,
                limit)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
#pragma warning restore EF1002
        return [.. rows.Select(row => SagaReader<AgentsDbContext>.ToSnapshot<TData>(row, []))];
    }
}
