using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;

namespace WalletPK.TopupMerchant.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for the Ledger: reads ledger.commands and answers on ledger.replies, as the real one does
/// (that side is tested in the Ledger's own suite): holds for top-ups, and merchant wallets, payments and
/// settlements. Tests can make it refuse, stay silent, and read every command it got.
/// </summary>
public sealed class FakeLedger : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, (string Code, string Reason)> _refusals = new();
    private readonly ConcurrentDictionary<Guid, int> _silent = new();
    private readonly ConcurrentDictionary<Guid, (string Code, string Reason)> _rejected = new();
    private readonly ConcurrentDictionary<Guid, Guid> _holdOwners = new();
    private readonly ConcurrentDictionary<Guid, string> _holdStates = new();
    private readonly ConcurrentDictionary<Guid, Guid> _merchantWallets = new();
    private readonly ConcurrentDictionary<Guid, bool> _refuseSettlements = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly JsonEventSerializer _serializer;
    private KafkaEventPublisher? _publisher;
    private Task? _loop;

    public FakeLedger()
    {
        _serializer = new JsonEventSerializer(new IntegrationEventRegistry().RegisterFromAssembly(typeof(PlaceHold).Assembly, typeof(PlaceHold).Namespace));
    }

    /// <summary>Every command received, by its ID (hold, payment, settlement or merchant), in order.</summary>
    public ConcurrentDictionary<Guid, ConcurrentQueue<IntegrationEvent>> Commands { get; } = new();

    /// <summary>Payments that moved money, by payment ID (each at most once).</summary>
    public ConcurrentDictionary<Guid, PayMerchant> Paid { get; } = new();

    /// <summary>Settlements posted, by settlement ID (each at most once).</summary>
    public ConcurrentDictionary<Guid, SettleMerchant> Settled { get; } = new();

    /// <summary>Refuses the user's holds and payments (until <see cref="Allow"/>).</summary>
    public void Refuse(Guid userId, string code, string reason) => _refusals[userId] = (code, reason);

    public void Allow(Guid userId) => _refusals.TryRemove(userId, out _);

    /// <summary>Refuses settlements for the merchant (until <see cref="AllowSettlements"/>).</summary>
    public void RefuseSettlements(Guid merchantId) => _refuseSettlements[merchantId] = true;

    public void AllowSettlements(Guid merchantId) => _refuseSettlements.TryRemove(merchantId, out _);

    /// <summary>Ignores the next <paramref name="count"/> commands for the user's holds and payments (a Ledger that is down).</summary>
    public void IgnoreNext(Guid userId, int count) => _silent[userId] = count;

    public IReadOnlyList<IntegrationEvent> CommandsFor(Guid id) =>
        Commands.TryGetValue(id, out var commands) ? [.. commands] : [];

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

    private bool Silenced(Guid userId) =>
        _silent.TryGetValue(userId, out var remaining) && remaining > 0 && _silent.TryUpdate(userId, remaining - 1, remaining);

    private IntegrationEvent? Answer(IntegrationEvent command) => command switch
    {
        OpenMerchantWallet open => Open(open),
        PayMerchant pay => Pay(pay),
        SettleMerchant settle => Settle(settle),
        PlaceHold or CaptureHold or ReleaseHold => AnswerHold(command),
        _ => null,
    };

    private MerchantWalletOpened Open(OpenMerchantWallet open)
    {
        Commands.GetOrAdd(open.MerchantId, _ => new()).Enqueue(open);
        var wallet = _merchantWallets.GetOrAdd(open.OwnerUserId, _ => Guid.NewGuid());
        return new MerchantWalletOpened(open.MerchantId, wallet, open.MerchantCode);
    }

    /// <summary>As the real Ledger: one journal per payment ID, and a refusal is final.</summary>
    private IntegrationEvent? Pay(PayMerchant pay)
    {
        Commands.GetOrAdd(pay.PaymentId, _ => new()).Enqueue(pay);
        if (Silenced(pay.PayerUserId))
        {
            return null;
        }

        if (_rejected.TryGetValue(pay.PaymentId, out var rejected))
        {
            return new FundsTransferRejected(pay.PaymentId, pay.ReplyTo, rejected.Code, rejected.Reason);
        }

        if (!Paid.ContainsKey(pay.PaymentId) && _refusals.TryGetValue(pay.PayerUserId, out var refusal))
        {
            _rejected[pay.PaymentId] = refusal;
            return new FundsTransferRejected(pay.PaymentId, pay.ReplyTo, refusal.Code, refusal.Reason);
        }

        Paid.TryAdd(pay.PaymentId, pay);
        return new MerchantPaid(pay.PaymentId, pay.ReplyTo, pay.AmountPaisa, pay.FeePaisa, pay.TaxPaisa, pay.AmountPaisa - pay.FeePaisa - pay.TaxPaisa, DateTimeOffset.UtcNow);
    }

    private IntegrationEvent Settle(SettleMerchant settle)
    {
        Commands.GetOrAdd(settle.SettlementId, _ => new()).Enqueue(settle);
        if (_rejected.TryGetValue(settle.SettlementId, out var rejected))
        {
            return new FundsTransferRejected(settle.SettlementId, settle.MerchantId, rejected.Code, rejected.Reason);
        }

        if (!Settled.ContainsKey(settle.SettlementId) && _refuseSettlements.ContainsKey(settle.MerchantId))
        {
            _rejected[settle.SettlementId] = ("merchant.settlement_invalid", "Refused (test)");
            return new FundsTransferRejected(settle.SettlementId, settle.MerchantId, "merchant.settlement_invalid", "Refused (test)");
        }

        Settled.TryAdd(settle.SettlementId, settle);
        return new MerchantSettled(settle.SettlementId, settle.MerchantId, settle.AmountPaisa, DateTimeOffset.UtcNow);
    }

    private IntegrationEvent? AnswerHold(IntegrationEvent command)
    {
        var holdId = command switch
        {
            PlaceHold place => place.HoldId,
            CaptureHold capture => capture.HoldId,
            ReleaseHold release => release.HoldId,
            _ => Guid.Empty,
        };

        Commands.GetOrAdd(holdId, _ => new()).Enqueue(command);
        if (command is PlaceHold placing)
        {
            _holdOwners.TryAdd(holdId, placing.UserId);
        }

        if (Silenced(_holdOwners.GetValueOrDefault(holdId)))
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
