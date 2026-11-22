using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Serialization;
using WalletPK.Notifications.Application;
using WalletPK.Notifications.Infrastructure.Persistence;
using WalletPK.Persistence.Inbox;
using WalletPK.Resilience;

namespace WalletPK.Notifications.Infrastructure;

/// <summary>SMS and email gateway settings, from "Providers:Messaging".</summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Providers:Messaging";

    /// <summary>Where the gateways are (the mock providers service in development).</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Sent as X-Api-Key.</summary>
    public string ApiKey { get; set; } = "";

    public ProviderResilienceOptions Resilience { get; set; } = new();

    public bool IsValid() => BaseUrl is { IsAbsoluteUri: true } && ApiKey.Length > 0 && Resilience.IsValid();
}

/// <summary>
/// The SMS and email gateways over HTTP, each behind its own resilience pipeline (bulkhead, timeouts, retry
/// with jitter, circuit breaker). The delivery's ID is the message ID, so the gateway's own idempotency
/// covers the retries.
/// </summary>
internal sealed partial class MessageGateway(HttpClient http, MessagingPipelines pipelines, ILogger<MessageGateway> logger) : IMessageGateway
{
    public Task<GatewayResult> SendSmsAsync(Guid messageId, string to, string text, CancellationToken cancellationToken) =>
        SendAsync("sms", "sms/v1/messages", new { messageId = messageId.ToString(), to, text }, cancellationToken);

    public Task<GatewayResult> SendEmailAsync(Guid messageId, string to, string subject, string body, CancellationToken cancellationToken) =>
        SendAsync("email", "email/v1/messages", new { messageId = messageId.ToString(), to, subject, body }, cancellationToken);

    private async Task<GatewayResult> SendAsync(string gateway, string path, object body, CancellationToken cancellationToken)
    {
        var result = await pipelines.Pipelines.CallAsync(gateway, token => http.PostAsJsonAsync(path, body, JsonSerializerOptions.Web, token), cancellationToken);
        switch (result.Outcome)
        {
            case ProviderOutcome.Ok:
                return new(GatewayOutcome.Sent, result.Body.TryGetProperty("reference", out var reference) ? reference.GetString() : null, null);
            case ProviderOutcome.Rejected:
                LogRefused(logger, gateway, result.Detail);
                return new(GatewayOutcome.Rejected, null, result.Detail ?? result.Code);
            default:
                LogUnavailable(logger, gateway, result.Outcome, result.Detail);
                return new(GatewayOutcome.Unavailable, null, result.Detail ?? result.Outcome.ToString());
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Gateway} gateway refused a message: {Detail}")]
    private static partial void LogRefused(ILogger logger, string gateway, string? detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Gateway} gateway is unavailable ({Outcome}: {Detail}); the message will be retried")]
    private static partial void LogUnavailable(ILogger logger, string gateway, ProviderOutcome outcome, string? detail);
}

/// <summary>One pipeline per gateway; metric walletpk.notifications.gateway.circuit_state.</summary>
public sealed class MessagingPipelines(MessagingOptions options, ILoggerFactory loggerFactory, IMeterFactory meterFactory)
{
    public const string MeterName = "WalletPK.Notifications";

    public ProviderPipelines Pipelines { get; } = new(
        "messaging", ["sms", "email"], options.Resilience, loggerFactory, meterFactory, MeterName, "walletpk.notifications.gateway.circuit_state", "gateway");
}

/// <summary>
/// Sends due SMS and email in the background: woken as soon as an event queues one, otherwise every
/// <see cref="NotificationsOptions.SenderPollMilliseconds"/> for retries.
/// </summary>
internal sealed partial class DeliverySender(IServiceScopeFactory scopes, DeliverySignal signal, NotificationsOptions options, ILogger<DeliverySender> logger) : BackgroundService
{
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                sent = await scope.ServiceProvider.GetRequiredService<DeliveryService>().SendDueAsync(BatchSize, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, exception);
            }

            if (sent < BatchSize)
            {
                await signal.WaitAsync(TimeSpan.FromMilliseconds(options.SenderPollMilliseconds), stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending notifications failed; trying again shortly")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Processes one consumed event through the inbox (exactly once, one transaction), and only once that has
/// committed sends the live pushes the handlers queued and wakes the SMS and email sender.
/// </summary>
internal sealed partial class NotificationsEnvelopeProcessor(
    InboxEnvelopeProcessor<NotificationsDbContext> inbox,
    RealtimeOutbox outbox,
    IRealtime realtime,
    DeliverySignal signal,
    ILogger<NotificationsEnvelopeProcessor> logger) : IEnvelopeProcessor
{
    public async Task ProcessAsync(EventEnvelope envelope, string consumer, CancellationToken cancellationToken)
    {
        outbox.Clear();
        try
        {
            await inbox.ProcessAsync(envelope, consumer, cancellationToken);
        }
        catch
        {
            outbox.Clear();
            throw;
        }

        var wake = outbox.DeliveriesQueued;
        foreach (var push in outbox.Take())
        {
            try
            {
                await push(realtime);
            }
            catch (Exception exception)
            {
                // Live pushes are best effort: the app reloads over HTTP when it reconnects.
                LogPushFailed(logger, envelope.EventType, exception);
            }
        }

        if (wake)
        {
            signal.Wake();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A live push for {EventType} failed")]
    private static partial void LogPushFailed(ILogger logger, string eventType, Exception exception);
}
