using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using WalletPK.Notifications.Application;
using WalletPK.Web.Identity;

namespace WalletPK.Notifications.Api.Realtime;

/// <summary>
/// Live updates for the signed-in customer, merchant or agent (M4.7, M11.1), at /hubs/wallet (through the
/// gateway too). The browser only listens. Connect with the access token (SignalR sends it as
/// ?access_token= on WebSockets).
/// </summary>
public sealed class WalletHub : Hub<IWalletClient>
{
    public const string Path = "/hubs/wallet";
}

/// <summary>What the server calls on a user's browsers.</summary>
public interface IWalletClient
{
    /// <summary>A wallet of the user got a new entry. Ignore it when <c>sequence</c> is not newer than what is shown.</summary>
    Task EntryPosted(LiveWalletEntry entry);

    /// <summary>A new notification, with the new unread count.</summary>
    Task NotificationReceived(LiveNotification notification);

    /// <summary>One of the user's payments changed status: refresh it.</summary>
    Task PaymentChanged(PaymentUpdate update);
}

/// <summary>
/// The Ops live feed (M11.1) for back-office staff, at /hubs/ops: fraud alerts, freezes and cases, stuck
/// payments, agents running out of float, as they happen.
/// </summary>
public sealed class OpsHub : Hub<IOpsClient>
{
    public const string Path = "/hubs/ops";
}

public interface IOpsClient
{
    /// <summary>Something staff should see now.</summary>
    Task Event(OpsEvent opsEvent);
}

/// <summary>
/// Pushes through the hubs. With the Redis backplane, a push from any instance reaches the user's browsers
/// on every instance.
/// </summary>
internal sealed class SignalRRealtime(IHubContext<WalletHub, IWalletClient> wallet, IHubContext<OpsHub, IOpsClient> ops) : IRealtime
{
    public Task EntryPostedAsync(Guid userId, LiveWalletEntry entry) => wallet.Clients.User(userId.ToString()).EntryPosted(entry);

    public Task NotificationAsync(Guid userId, LiveNotification notification) => wallet.Clients.User(userId.ToString()).NotificationReceived(notification);

    public Task PaymentChangedAsync(Guid userId, PaymentUpdate update) => wallet.Clients.User(userId.ToString()).PaymentChanged(update);

    public Task OpsAsync(OpsEvent opsEvent) => ops.Clients.All.Event(opsEvent);
}

/// <summary>SignalR addresses users by the access token's "sub" claim, the same ID the rest of the API uses.</summary>
internal sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection?.User.FindFirst(WalletPkClaimTypes.Subject)?.Value;
}

/// <summary>
/// Live pushes go out from here, in order, in the background: event processing never waits for SignalR or
/// the Redis backplane. If the backplane is slow or down, pushes queue up (the oldest are dropped past
/// 10,000) and the app catches up over HTTP when it reconnects.
/// </summary>
internal sealed partial class LivePushQueue(SignalRRealtime hubs, ILogger<LivePushQueue> logger) : BackgroundService, IRealtime
{
    private readonly Channel<Func<IRealtime, Task>> _queue = Channel.CreateBounded<Func<IRealtime, Task>>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public Task EntryPostedAsync(Guid userId, LiveWalletEntry entry) => Enqueue(realtime => realtime.EntryPostedAsync(userId, entry));

    public Task NotificationAsync(Guid userId, LiveNotification notification) => Enqueue(realtime => realtime.NotificationAsync(userId, notification));

    public Task PaymentChangedAsync(Guid userId, PaymentUpdate update) => Enqueue(realtime => realtime.PaymentChangedAsync(userId, update));

    public Task OpsAsync(OpsEvent opsEvent) => Enqueue(realtime => realtime.OpsAsync(opsEvent));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var push in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await push(hubs);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogPushFailed(logger, exception);
            }
        }
    }

    private Task Enqueue(Func<IRealtime, Task> push)
    {
        _queue.Writer.TryWrite(push);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A live push failed (the app reloads over HTTP when it reconnects)")]
    private static partial void LogPushFailed(ILogger logger, Exception exception);
}
