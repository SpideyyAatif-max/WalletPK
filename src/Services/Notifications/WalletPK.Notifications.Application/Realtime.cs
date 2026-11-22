using System.Threading.Channels;

namespace WalletPK.Notifications.Application;

/// <summary>
/// Live pushes waiting for the database: an event's handlers queue them here, and they go out only after the
/// event's transaction has committed. A browser told about a notification can then always load it.
/// </summary>
public sealed class RealtimeOutbox
{
    private readonly List<Func<IRealtime, Task>> _pending = [];

    /// <summary>Some SMS or email was queued, so the sender should look now.</summary>
    public bool DeliveriesQueued { get; private set; }

    public void Push(Func<IRealtime, Task> push)
    {
        ArgumentNullException.ThrowIfNull(push);
        _pending.Add(push);
    }

    public void DeliveryQueued() => DeliveriesQueued = true;

    /// <summary>Takes everything queued (after the commit).</summary>
    public IReadOnlyList<Func<IRealtime, Task>> Take()
    {
        List<Func<IRealtime, Task>> taken = [.. _pending];
        _pending.Clear();
        DeliveriesQueued = false;
        return taken;
    }

    /// <summary>Drops everything queued (the transaction rolled back).</summary>
    public void Clear()
    {
        _pending.Clear();
        DeliveriesQueued = false;
    }
}

/// <summary>Wakes the SMS and email sender as soon as something is queued, instead of at its next poll.</summary>
public sealed class DeliverySignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Wake() => _channel.Writer.TryWrite(true);

    /// <summary>Waits until woken or <paramref name="timeout"/> passes.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Poll interval reached.
        }
    }
}
