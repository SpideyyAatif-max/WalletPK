using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Agents.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for the Ledger: reads ledger.commands and answers agent commands on ledger.replies, with
/// balances, as the real one does (that side is tested in the Ledger's own suite). Customer wallets are
/// added by tests; agent floats are opened by OpenAgentWallet. Each command posts at most once and a
/// refusal is final, by its ID.
/// </summary>
public sealed class FakeLedger : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Guid> _ownersByNumber = [];
    private readonly Dictionary<Guid, long> _wallets = [];
    private readonly Dictionary<Guid, long> _floats = [];
    private readonly Dictionary<Guid, IntegrationEvent> _answers = [];
    private readonly ConcurrentDictionary<Guid, int> _silent = new();
    private readonly ConcurrentDictionary<Guid, bool> _refusePayouts = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly JsonEventSerializer _serializer;
    private KafkaEventPublisher? _publisher;
    private Task? _loop;

    public FakeLedger()
    {
        _serializer = new JsonEventSerializer(new IntegrationEventRegistry().RegisterFromAssembly(typeof(OpenAgentWallet).Assembly, typeof(OpenAgentWallet).Namespace));
    }

    /// <summary>Every command received, by its ID (agent, transaction or payout), in order.</summary>
    public ConcurrentDictionary<Guid, ConcurrentQueue<IntegrationEvent>> Commands { get; } = new();

    /// <summary>Commission payouts posted, by payout ID.</summary>
    public ConcurrentDictionary<Guid, PayAgentCommission> Paid { get; } = new();

    /// <summary>Adds a customer wallet with <paramref name="paisa"/> in it.</summary>
    public void AddCustomer(Guid userId, string walletNumber, long paisa = 0)
    {
        lock (_gate)
        {
            _ownersByNumber[walletNumber] = userId;
            _wallets[userId] = paisa;
        }
    }

    public void Deposit(Guid userId, long paisa)
    {
        lock (_gate)
        {
            _wallets[userId] += paisa;
        }
    }

    public long Wallet(Guid userId)
    {
        lock (_gate)
        {
            return _wallets[userId];
        }
    }

    public long Float(Guid ownerUserId)
    {
        lock (_gate)
        {
            return _floats.GetValueOrDefault(ownerUserId);
        }
    }

    /// <summary>Ignores the next <paramref name="count"/> commands for <paramref name="agentOwnerUserId"/>'s transactions (a Ledger that is down).</summary>
    public void IgnoreNext(Guid agentOwnerUserId, int count) => _silent[agentOwnerUserId] = count;

    /// <summary>Refuses commission payouts for the agent (until <see cref="AllowPayouts"/>).</summary>
    public void RefusePayouts(Guid agentId) => _refusePayouts[agentId] = true;

    public void AllowPayouts(Guid agentId) => _refusePayouts.TryRemove(agentId, out _);

    public IReadOnlyList<IntegrationEvent> CommandsFor(Guid id) =>
        Commands.TryGetValue(id, out var commands) ? [.. commands] : [];

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

    private bool Silenced(Guid agentOwnerUserId) =>
        _silent.TryGetValue(agentOwnerUserId, out var remaining) && remaining > 0 && _silent.TryUpdate(agentOwnerUserId, remaining - 1, remaining);

    private IntegrationEvent? Answer(IntegrationEvent command)
    {
        var (id, owner) = command switch
        {
            OpenAgentWallet open => (open.AgentId, open.OwnerUserId),
            AgentCashIn cashIn => (cashIn.TransactionId, cashIn.AgentOwnerUserId),
            AgentCashOut cashOut => (cashOut.TransactionId, cashOut.AgentOwnerUserId),
            MoveAgentFloat move => (move.TransferId, move.AgentOwnerUserId),
            PayAgentCommission pay => (pay.PayoutId, pay.AgentOwnerUserId),
            _ => (Guid.Empty, Guid.Empty),
        };
        if (id == Guid.Empty)
        {
            return null;
        }

        Commands.GetOrAdd(id, _ => new()).Enqueue(command);
        if (command is not OpenAgentWallet and not PayAgentCommission && Silenced(owner))
        {
            return null;
        }

        lock (_gate)
        {
            if (_answers.TryGetValue(id, out var answered))
            {
                return answered;
            }

            var answer = command switch
            {
                OpenAgentWallet open => Open(open),
                AgentCashIn cashIn => CashIn(cashIn),
                AgentCashOut cashOut => CashOut(cashOut),
                MoveAgentFloat move => Move(move),
                PayAgentCommission pay => Pay(pay),
                _ => null,
            };

            // Opening is answered again every time; everything else once, then from memory.
            if (answer is not null && command is not OpenAgentWallet)
            {
                _answers[id] = answer;
            }

            return answer;
        }
    }

    private AgentWalletOpened Open(OpenAgentWallet open)
    {
        _floats.TryAdd(open.OwnerUserId, 0);
        return new AgentWalletOpened(open.AgentId, open.OwnerUserId, open.AgentCode);
    }

    private IntegrationEvent CashIn(AgentCashIn command)
    {
        if (!_floats.TryGetValue(command.AgentOwnerUserId, out var agentFloat))
        {
            return Refuse(command.TransactionId, command.ReplyTo, "agent.no_float", "No float.");
        }

        if (!_ownersByNumber.TryGetValue(command.CustomerWalletNumber, out var customer))
        {
            return Refuse(command.TransactionId, command.ReplyTo, "wallet.recipient_not_found", "No WalletPK wallet has this number.");
        }

        if (customer == command.AgentOwnerUserId)
        {
            return Refuse(command.TransactionId, command.ReplyTo, "agent.own_wallet", "Own wallet.");
        }

        if (agentFloat < command.AmountPaisa)
        {
            return Refuse(command.TransactionId, command.ReplyTo, "agent.float_insufficient", "Not enough float for this cash-in. Load your float first.");
        }

        _floats[command.AgentOwnerUserId] = agentFloat - command.AmountPaisa;
        _wallets[customer] += command.AmountPaisa;
        return new AgentCashedIn(command.TransactionId, command.ReplyTo, customer, Mask(command.CustomerWalletNumber), command.AmountPaisa, command.CommissionPaisa, _floats[command.AgentOwnerUserId], DateTimeOffset.UtcNow);
    }

    private IntegrationEvent CashOut(AgentCashOut command)
    {
        var total = command.AmountPaisa + command.FeePaisa + command.TaxPaisa;
        if (!_floats.ContainsKey(command.AgentOwnerUserId) || !_wallets.TryGetValue(command.CustomerUserId, out var balance))
        {
            return Refuse(command.TransactionId, command.ReplyTo, "wallet.not_found", "No wallet.");
        }

        if (balance < total)
        {
            return Refuse(command.TransactionId, command.ReplyTo, "wallet.insufficient_balance", "The customer does not have enough money for this cash-out and its fee.");
        }

        _wallets[command.CustomerUserId] = balance - total;
        _floats[command.AgentOwnerUserId] += command.AmountPaisa;
        return new AgentCashedOut(command.TransactionId, command.ReplyTo, command.AmountPaisa, command.FeePaisa, command.TaxPaisa, command.CommissionPaisa, _floats[command.AgentOwnerUserId], DateTimeOffset.UtcNow);
    }

    private IntegrationEvent Move(MoveAgentFloat command)
    {
        if (!_floats.TryGetValue(command.AgentOwnerUserId, out var agentFloat) || !_wallets.TryGetValue(command.AgentOwnerUserId, out var wallet))
        {
            return Refuse(command.TransferId, command.ReplyTo, "agent.no_float", "Both your wallet and your float are needed.");
        }

        var toFloat = command.Direction == MoveAgentFloat.ToFloat;
        if ((toFloat ? wallet : agentFloat) < command.AmountPaisa)
        {
            return toFloat
                ? Refuse(command.TransferId, command.ReplyTo, "wallet.insufficient_balance", "Not enough money in your wallet.")
                : Refuse(command.TransferId, command.ReplyTo, "agent.float_insufficient", "Not enough float.");
        }

        var change = toFloat ? command.AmountPaisa : -command.AmountPaisa;
        _floats[command.AgentOwnerUserId] = agentFloat + change;
        _wallets[command.AgentOwnerUserId] = wallet - change;
        return new AgentFloatMoved(command.TransferId, command.ReplyTo, command.Direction, command.AmountPaisa, _floats[command.AgentOwnerUserId], DateTimeOffset.UtcNow);
    }

    private IntegrationEvent Pay(PayAgentCommission command)
    {
        if (_refusePayouts.ContainsKey(command.AgentId))
        {
            return Refuse(command.PayoutId, command.AgentId, "agent.payout_invalid", "Refused (test)");
        }

        var net = command.GrossPaisa - command.TaxPaisa;
        _floats[command.AgentOwnerUserId] += net;
        Paid[command.PayoutId] = command;
        return new AgentCommissionPaid(command.PayoutId, command.AgentId, command.GrossPaisa, command.TaxPaisa, net, DateTimeOffset.UtcNow);
    }

    private static FundsTransferRejected Refuse(Guid id, Guid replyTo, string code, string reason) => new(id, replyTo, code, reason);

    private static string Mask(string number) => $"{number[..^7]}•••{number[^4..]}";

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
