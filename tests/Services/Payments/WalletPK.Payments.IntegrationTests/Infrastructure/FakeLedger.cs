using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Payments.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for the Ledger: reads ledger.commands and answers on ledger.replies, as the real one does
/// (that side is tested in the Ledger's own suite). Tests can make it refuse holds, fail captures or stay
/// silent for a user, and read every command it got.
/// </summary>
public sealed class FakeLedger : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, (string Code, string Reason)> _refusals = new();
    private readonly ConcurrentDictionary<Guid, bool> _failCaptures = new();
    private readonly ConcurrentDictionary<Guid, int> _silent = new();
    private readonly ConcurrentDictionary<Guid, (string Code, string Reason)> _transferRefusals = new();
    private readonly ConcurrentDictionary<Guid, (string Code, string Reason)> _rejectedTransfers = new();
    private readonly ConcurrentDictionary<Guid, Guid> _holdOwners = new();
    private readonly ConcurrentDictionary<Guid, string> _holdStates = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly JsonEventSerializer _serializer;
    private KafkaEventPublisher? _publisher;
    private Task? _loop;

    public FakeLedger()
    {
        _serializer = new JsonEventSerializer(new IntegrationEventRegistry().RegisterFromAssembly(typeof(PlaceHold).Assembly, typeof(PlaceHold).Namespace));
    }

    /// <summary>Every command received, by hold ID, in order.</summary>
    public ConcurrentDictionary<Guid, ConcurrentQueue<IntegrationEvent>> Commands { get; } = new();

    public void Refuse(Guid userId, string code, string reason) => _refusals[userId] = (code, reason);

    /// <summary>Refuses the user's wallet transfers (TransferFunds) until <see cref="AllowTransfers"/>.</summary>
    public void RefuseTransfers(Guid userId, string code, string reason) => _transferRefusals[userId] = (code, reason);

    public void AllowTransfers(Guid userId) => _transferRefusals.TryRemove(userId, out _);

    /// <summary>The fee and tax every wallet transfer is charged.</summary>
    public (long Fee, long Tax) TransferCharges { get; set; } = (1_000, 160);

    /// <summary>Transfers that moved money, by transfer ID (each at most once).</summary>
    public ConcurrentDictionary<Guid, TransferFunds> Transferred { get; } = new();

    public void FailCaptures(Guid userId) => _failCaptures[userId] = true;

    /// <summary>Ignores the next <paramref name="count"/> commands for the user's holds (a Ledger that is down).</summary>
    public void IgnoreNext(Guid userId, int count) => _silent[userId] = count;

    public IReadOnlyList<IntegrationEvent> CommandsFor(Guid holdId) =>
        Commands.TryGetValue(holdId, out var commands) ? [.. commands] : [];

    public string? HoldState(Guid holdId) => _holdStates.GetValueOrDefault(holdId);

    public void Start(string bootstrapServers)
    {
        _publisher = new KafkaEventPublisher(new KafkaOptions { BootstrapServers = bootstrapServers, ClientId = "fake-ledger" }, NullLogger<KafkaEventPublisher>.Instance);
        _loop = Task.Factory.StartNew(() => Run(bootstrapServers), TaskCreationOptions.LongRunning).Unwrap();
    }

    private async Task Run(string bootstrapServers)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = "fake-ledger",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
        }).Build();
        consumer.Subscribe(KafkaTopics.LedgerCommands);

        while (!_stop.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result;
            try
            {
                result = consumer.Consume(TimeSpan.FromMilliseconds(100));
            }
            catch (ConsumeException)
            {
                // The topic may not exist until the service under test creates it.
                await Task.Delay(200);
                continue;
            }

            if (result?.Message is null
                || !KafkaMessages.TryFromMessage(result.Topic, result.Message, out var envelope, out _)
                || !_serializer.TryUnwrap(envelope!, out var command))
            {
                continue;
            }

            var reply = Answer(command);
            if (reply is not null)
            {
                await _publisher!.PublishAsync(_serializer.Wrap(reply, envelope!.CorrelationId, envelope.EventId.ToString()));
            }
        }

        consumer.Close();
    }

    private IntegrationEvent? Answer(IntegrationEvent command)
    {
        if (command is TransferFunds transfer)
        {
            return AnswerTransfer(transfer);
        }

        var holdId = command switch
        {
            PlaceHold place => place.HoldId,
            CaptureHold capture => capture.HoldId,
            ReleaseHold release => release.HoldId,
            _ => Guid.Empty,
        };
        if (holdId == Guid.Empty)
        {
            return null;
        }

        Commands.GetOrAdd(holdId, _ => new()).Enqueue(command);
        if (command is PlaceHold placing)
        {
            _holdOwners.TryAdd(holdId, placing.UserId);
        }

        var owner = _holdOwners.GetValueOrDefault(holdId);
        if (_silent.TryGetValue(owner, out var remaining) && remaining > 0 && _silent.TryUpdate(owner, remaining - 1, remaining))
        {
            return null;
        }

        switch (command)
        {
            case PlaceHold place when _refusals.TryGetValue(place.UserId, out var refusal):
                _holdStates[holdId] = "Rejected";
                return new HoldRejected(holdId, refusal.Code, refusal.Reason);
            case PlaceHold place:
                _holdStates.TryAdd(holdId, "Active");
                return new HoldPlaced(holdId, Guid.NewGuid(), place.AmountPaisa, DateTimeOffset.UtcNow.AddSeconds(place.LifetimeSeconds));
            case CaptureHold capture when _failCaptures.ContainsKey(owner):
                return new HoldCaptureFailed(holdId, capture.TransactionId, "wallet.insufficient_balance", "Not enough money (test)");
            case CaptureHold capture:
                _holdStates[holdId] = "Captured";
                return new HoldCaptured(holdId, capture.TransactionId, capture.Credits.Sum(credit => credit.AmountPaisa));
            case ReleaseHold:
                var state = _holdStates.GetValueOrDefault(holdId);
                if (state == "Captured")
                {
                    return null;
                }

                _holdStates[holdId] = "Released";
                return new HoldReleased(holdId, state is "Active" or "Released" ? "Released" : "NotPlaced");
            default:
                return null;
        }
    }

    /// <summary>As the real Ledger: one journal per transfer ID, and a refusal is final.</summary>
    private IntegrationEvent? AnswerTransfer(TransferFunds transfer)
    {
        Commands.GetOrAdd(transfer.TransferId, _ => new()).Enqueue(transfer);
        if (_silent.TryGetValue(transfer.SenderUserId, out var remaining) && remaining > 0 && _silent.TryUpdate(transfer.SenderUserId, remaining - 1, remaining))
        {
            return null;
        }

        if (_rejectedTransfers.TryGetValue(transfer.TransferId, out var rejected))
        {
            return new FundsTransferRejected(transfer.TransferId, transfer.ReplyTo, rejected.Code, rejected.Reason);
        }

        if (!Transferred.ContainsKey(transfer.TransferId) && _transferRefusals.TryGetValue(transfer.SenderUserId, out var refusal))
        {
            _rejectedTransfers[transfer.TransferId] = refusal;
            return new FundsTransferRejected(transfer.TransferId, transfer.ReplyTo, refusal.Code, refusal.Reason);
        }

        Transferred.TryAdd(transfer.TransferId, transfer);
        return new FundsTransferred(transfer.TransferId, transfer.ReplyTo, transfer.AmountPaisa, TransferCharges.Fee, TransferCharges.Tax, transfer.ToWalletNumber, DateTimeOffset.UtcNow);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }

        _publisher?.Dispose();
        _stop.Dispose();
    }
}
