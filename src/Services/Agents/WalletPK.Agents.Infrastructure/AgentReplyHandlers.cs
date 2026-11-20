using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalletPK.Agents.Application;
using WalletPK.Agents.Domain;
using WalletPK.Agents.Infrastructure.Persistence;
using WalletPK.Contracts.Agents;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Agents.Infrastructure;

/// <summary>
/// The Ledger's answers about agents that are not Saga replies: a float opened (the agent becomes Active),
/// a commission payout posted or refused. They run in the inbox's transaction, which saves.
/// </summary>
internal sealed partial class AgentReplyHandlers(
    AgentsDbContext db,
    JsonEventSerializer serializer,
    TimeProvider timeProvider,
    ILogger<AgentReplyHandlers> logger)
    : IIntegrationEventHandler<AgentWalletOpened>, IIntegrationEventHandler<AgentCommissionPaid>, IIntegrationEventHandler<FundsTransferRejected>
{
    public async Task HandleAsync(AgentWalletOpened integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(context);
        if (await db.Set<Agent>().SingleOrDefaultAsync(agent => agent.Id == integrationEvent.AgentId, cancellationToken) is not { Status: AgentStatus.Opening } agent)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        agent.WalletId = integrationEvent.WalletId;
        agent.Status = AgentStatus.Active;
        agent.UpdatedAt = now;
        db.AddToOutbox(serializer, new AgentStatusChanged(agent.Id, agent.OwnerUserId, agent.Code, agent.Status, null, now), now, context.CorrelationId, context.EventId.ToString());
        LogActive(logger, agent.Code);
    }

    public async Task HandleAsync(AgentCommissionPaid integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(context);
        if (await PendingAsync(integrationEvent.PayoutId, cancellationToken) is not { } payout)
        {
            return;
        }

        payout.Status = "Paid";
        payout.PaidAt = integrationEvent.PostedAt;
        Publish(payout, context);
    }

    /// <summary>A refused payout (other refusals are transactions, for the Saga): its commissions are freed for the next run.</summary>
    public async Task HandleAsync(FundsTransferRejected integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(context);
        if (await PendingAsync(integrationEvent.TransferId, cancellationToken) is not { } payout)
        {
            return;
        }

        payout.Status = "Failed";
        var reason = $"{integrationEvent.Code}: {integrationEvent.Reason}";
        payout.FailureReason = reason[..Math.Min(200, reason.Length)];
        await db.Set<PaidCommission>().Where(paid => paid.PayoutId == payout.Id).ExecuteDeleteAsync(cancellationToken);
        LogPayoutRefused(logger, payout.Id, integrationEvent.Code);
        Publish(payout, context);
    }

    private Task<CommissionPayout?> PendingAsync(Guid id, CancellationToken cancellationToken) =>
        db.Set<CommissionPayout>().SingleOrDefaultAsync(payout => payout.Id == id && payout.Status == "Pending", cancellationToken);

    private void Publish(CommissionPayout payout, EventContext context) =>
        db.AddToOutbox(
            serializer,
            new AgentCommissionPayoutChanged(payout.Id, payout.AgentId, payout.BusinessDate, payout.TransactionCount, payout.GrossPaisa, payout.TaxPaisa, payout.NetPaisa, payout.Status, timeProvider.GetUtcNow()),
            timeProvider.GetUtcNow(),
            context.CorrelationId,
            context.EventId.ToString());

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {Code} is active")]
    private static partial void LogActive(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Commission payout {PayoutId} was refused ({Code}); its commissions go into the next run")]
    private static partial void LogPayoutRefused(ILogger logger, Guid payoutId, string code);
}
