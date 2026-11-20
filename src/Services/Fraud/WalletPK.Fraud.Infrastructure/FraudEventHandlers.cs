using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Contracts.Identity;
using WalletPK.Contracts.Ledger;
using WalletPK.Fraud.Application;
using WalletPK.Fraud.Infrastructure.Persistence;
using WalletPK.Messaging.Abstractions;

namespace WalletPK.Fraud.Infrastructure;

/// <summary>
/// What the Fraud service learns from other services' events: when users registered, devices were
/// trusted and stepped-up payments confirmed (identity.events), whose wallet number is whose and who paid whom (ledger.events), and the
/// Ledger's confirmation of a freeze (ledger.replies).
/// </summary>
internal sealed class FraudEventHandlers(IFeatureStore features, RingQueue rings, FraudDbContext db) :
    IIntegrationEventHandler<UserRegistered>,
    IIntegrationEventHandler<DeviceBound>,
    IIntegrationEventHandler<PaymentStepUpConfirmed>,
    IIntegrationEventHandler<WalletOpened>,
    IIntegrationEventHandler<WalletEntryPosted>,
    IIntegrationEventHandler<WalletsFreezeChanged>
{
    public Task HandleAsync(UserRegistered integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return features.SetAccountAsync(integrationEvent.UserId, integrationEvent.PhoneNumber, integrationEvent.RegisteredAt);
    }

    public Task HandleAsync(DeviceBound integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return features.SetDeviceAsync(integrationEvent.UserId, integrationEvent.DeviceId, integrationEvent.BoundAt, integrationEvent.CoolingOffUntil);
    }

    /// <summary>The user confirmed a stepped-up payment with a one-time code: their next one from that device goes through.</summary>
    public Task HandleAsync(PaymentStepUpConfirmed integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return features.ConfirmStepUpAsync(integrationEvent.UserId, integrationEvent.DeviceId, integrationEvent.ValidUntil);
    }

    public Task HandleAsync(WalletOpened integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return integrationEvent.WalletNumber is { } number ? features.SetOwnerAsync(number, integrationEvent.OwnerId) : Task.CompletedTask;
    }

    /// <summary>A P2P payment that went through: counts toward the recipient's fan-in and is checked for a ring.</summary>
    public async Task HandleAsync(WalletEntryPosted integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (integrationEvent is not
            {
                WalletType: "CustomerWallet",
                Direction: "Credit",
                JournalType: "P2PTransfer",
                WalletNumber: { } recipient,
                CounterpartyWalletNumber: { } senderNumber,
            })
        {
            return;
        }

        if (integrationEvent.OwnerId is { } owner)
        {
            await features.SetOwnerAsync(recipient, owner);
        }

        if (await features.OwnerOfAsync(senderNumber) is { } sender)
        {
            await features.AddFanInAsync(recipient, sender, integrationEvent.PostedAt);
            rings.Add(new RingObservation(recipient, sender, integrationEvent.PostedAt));
        }
    }

    public async Task HandleAsync(WalletsFreezeChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (await db.Set<AccountFlag>().SingleOrDefaultAsync(flag => flag.RequestId == integrationEvent.RequestId, cancellationToken) is { } flag)
        {
            flag.ConfirmedAt = integrationEvent.ChangedAt;
        }
    }
}

/// <summary>Works through the ring observations (M10.5), away from the payment path.</summary>
internal sealed partial class RingDetectionService(RingQueue queue, IServiceScopeFactory scopes, ILogger<RingDetectionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var observation in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (await scope.ServiceProvider.GetRequiredService<RingDetector>().ObserveAsync(observation, stoppingToken))
                    {
                        LogRing(logger, observation.Recipient);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogFailed(logger, observation.Recipient, exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mule ring detected around {Recipient}")]
    private static partial void LogRing(ILogger logger, string recipient);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ring detection failed for {Recipient}")]
    private static partial void LogFailed(ILogger logger, string recipient, Exception exception);
}
