using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalletPK.Contracts.Ledger;
using WalletPK.Contracts.TopupMerchant;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Outbox;
using WalletPK.TopupMerchant.Application.Merchants;
using WalletPK.TopupMerchant.Infrastructure.Persistence;

namespace WalletPK.TopupMerchant.Infrastructure.Merchants;

/// <summary>
/// The Ledger's answers about merchants (not Saga replies): a merchant wallet opened (the merchant becomes
/// Active), a settlement posted or refused. They run in the inbox's transaction, which saves.
/// </summary>
internal sealed partial class MerchantReplyHandlers(
    TopupMerchantDbContext db,
    JsonEventSerializer serializer,
    TimeProvider timeProvider,
    ILogger<MerchantReplyHandlers> logger)
    : IIntegrationEventHandler<MerchantWalletOpened>, IIntegrationEventHandler<MerchantSettled>, IIntegrationEventHandler<FundsTransferRejected>
{
    public async Task HandleAsync(MerchantWalletOpened integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (await db.Set<Merchant>().SingleOrDefaultAsync(merchant => merchant.Id == integrationEvent.MerchantId, cancellationToken) is { } merchant)
        {
            merchant.WalletId = integrationEvent.WalletId;
            merchant.Status = "Active";
            LogActive(logger, merchant.Code);
        }
    }

    public async Task HandleAsync(MerchantSettled integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(context);
        if (await PendingAsync(integrationEvent.SettlementId, cancellationToken) is not { } settlement)
        {
            return;
        }

        settlement.Status = "Settled";
        settlement.SettledAt = integrationEvent.PostedAt;
        Publish(settlement, context);
    }

    /// <summary>A refused settlement (other refusals are payments, for the Saga): its payments are freed for the next run.</summary>
    public async Task HandleAsync(FundsTransferRejected integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(context);
        if (await PendingAsync(integrationEvent.TransferId, cancellationToken) is not { } settlement)
        {
            return;
        }

        settlement.Status = "Failed";
        var reason = $"{integrationEvent.Code}: {integrationEvent.Reason}";
        settlement.FailureReason = reason[..Math.Min(200, reason.Length)];
        await db.Set<SettledPayment>().Where(settled => settled.SettlementId == settlement.Id).ExecuteDeleteAsync(cancellationToken);
        LogSettlementRefused(logger, settlement.Id, integrationEvent.Code);
        Publish(settlement, context);
    }

    private Task<MerchantSettlement?> PendingAsync(Guid id, CancellationToken cancellationToken) =>
        db.Set<MerchantSettlement>().SingleOrDefaultAsync(settlement => settlement.Id == id && settlement.Status == "Pending", cancellationToken);

    private void Publish(MerchantSettlement settlement, EventContext context) =>
        db.AddToOutbox(
            serializer,
            new MerchantSettlementChanged(settlement.Id, settlement.MerchantId, settlement.BusinessDate, settlement.PaymentCount, settlement.NetPaisa, settlement.Status, timeProvider.GetUtcNow()),
            timeProvider.GetUtcNow(),
            context.CorrelationId,
            context.EventId.ToString());

    [LoggerMessage(Level = LogLevel.Information, Message = "Merchant {Code} is active")]
    private static partial void LogActive(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settlement {SettlementId} was refused ({Code}); its payments go into the next run")]
    private static partial void LogSettlementRefused(ILogger logger, Guid settlementId, string code);
}
