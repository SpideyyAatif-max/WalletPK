using System.Globalization;
using WalletPK.Agents.Domain;
using WalletPK.Contracts.Agents;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging.Events;
using WalletPK.Sagas;

namespace WalletPK.Agents.Application;

/// <summary>A cash-in, cash-out or float move, stored as JSON in its saga_instances row.</summary>
public sealed class AgentTransactionData
{
    /// <summary>One of <see cref="AgentTransactionKind"/>.</summary>
    public string Kind { get; set; } = "";

    public Guid AgentId { get; set; }

    public Guid AgentOwnerUserId { get; set; }

    public string AgentCode { get; set; } = "";

    public string AgentName { get; set; } = "";

    /// <summary>The float alert level when the transaction started.</summary>
    public long LowFloatPaisa { get; set; }

    /// <summary>The customer (cash-in: known once the Ledger has found the wallet).</summary>
    public Guid? CustomerUserId { get; set; }

    public string? CustomerWalletNumber { get; set; }

    /// <summary>The customer's wallet name, as the Ledger has it (a masked number).</summary>
    public string? CustomerName { get; set; }

    /// <summary>The cash-out request this takes (cash-out only).</summary>
    public Guid? CashOutRequestId { get; set; }

    /// <summary>The customer's device when it asked for the cash-out (limits).</summary>
    public string? DeviceId { get; set; }

    public string IdempotencyKey { get; set; } = "";

    public long AmountPaisa { get; set; }

    public long FeePaisa { get; set; }

    public long TaxPaisa { get; set; }

    public long CommissionPaisa { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Processing, Completed, Failed or UnderReview.</summary>
    public string Status { get; set; } = "Processing";

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The agent's float right after (from the Ledger).</summary>
    public long? AgentBalancePaisa { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureReason { get; set; }

    public int LedgerResends { get; set; }
}

/// <summary>Agent transaction settings, from "Agents:Transactions".</summary>
public sealed class AgentTransactionOptions
{
    public const string SectionName = "Agents:Transactions";

    public int LedgerReplyTimeoutSeconds { get; set; } = 30;

    public int MaxLedgerResends { get; set; } = 60;

    public bool IsValid() => LedgerReplyTimeoutSeconds >= 1 && MaxLedgerResends >= 1;
}

/// <summary>
/// One agent transaction (M9.1-M9.3): a cash-in, a cash-out or a float move is one Ledger command, posted in
/// one journal with the commission accrued. The transaction ID is the journal ID and refusals are
/// remembered, so the command is sent again whenever no answer comes, and never posts twice or after a "no".
/// <code>Posting ──AgentCashedIn / AgentCashedOut / AgentFloatMoved──▶ Completed
///    └──FundsTransferRejected──▶ Failed
///    └──no answer after every resend──▶ NeedsReview</code>
/// After a cash-in that leaves the float below the agent's alert level, an agents.float_low event is sent.
/// </summary>
public sealed class AgentTransactionSaga : SagaDefinition<AgentTransactionData>
{
    public const string SagaName = "agent-transaction";

    private const string LedgerCheck = "ledger-check";

    private readonly AgentTransactionOptions _options;

    public AgentTransactionSaga(AgentTransactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        On<AgentCashedIn>((saga, reply) =>
        {
            if (!Waiting(saga))
            {
                return;
            }

            saga.Data.CustomerUserId = reply.CustomerUserId;
            saga.Data.CustomerName = reply.CustomerName;
            Done(saga, reply.AgentBalancePaisa, reply.PostedAt, $"{Rupees(reply.AmountPaisa)} deposited to {reply.CustomerName}");
            if (reply.AgentBalancePaisa < saga.Data.LowFloatPaisa)
            {
                saga.Send(new AgentFloatLow(saga.Data.AgentId, saga.Data.AgentOwnerUserId, reply.AgentBalancePaisa, saga.Data.LowFloatPaisa, saga.Now));
            }
        });
        On<AgentCashedOut>((saga, reply) =>
        {
            if (Waiting(saga))
            {
                Done(saga, reply.AgentBalancePaisa, reply.PostedAt, $"{Rupees(reply.AmountPaisa)} withdrawn; hand over the cash");
            }
        });
        On<AgentFloatMoved>((saga, reply) =>
        {
            if (Waiting(saga))
            {
                Done(saga, reply.AgentBalancePaisa, reply.PostedAt, reply.Direction == MoveAgentFloat.ToFloat ? $"{Rupees(reply.AmountPaisa)} loaded to your float" : $"{Rupees(reply.AmountPaisa)} moved to your wallet");
            }
        });
        On<FundsTransferRejected>((saga, reply) =>
        {
            if (!Waiting(saga))
            {
                return;
            }

            saga.Data.FailureCode = reply.Code;
            saga.Data.FailureReason = reply.Reason;
            Finish(saga, "Failed", $"Not done: {reply.Reason}");
        });
    }

    public override string Name => SagaName;

    protected override void OnStart(SagaContext<AgentTransactionData> saga)
    {
        var data = saga.Data;
        var detail = data.Kind switch
        {
            AgentTransactionKind.CashIn => $"Depositing {Rupees(data.AmountPaisa)} to {data.CustomerWalletNumber}",
            AgentTransactionKind.CashOut => $"Withdrawing {Rupees(data.AmountPaisa)} for {data.CustomerName}",
            AgentTransactionKind.FloatIn => $"Loading {Rupees(data.AmountPaisa)} to your float",
            _ => $"Moving {Rupees(data.AmountPaisa)} to your wallet",
        };
        saga.TransitionTo("Posting", detail);
        saga.Send(Command(saga));
        saga.WakeIn(TimeSpan.FromSeconds(_options.LedgerReplyTimeoutSeconds), LedgerCheck);
    }

    protected override Task OnWakeAsync(SagaContext<AgentTransactionData> saga, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (saga.WakeReason != LedgerCheck || saga.State != "Posting")
        {
            return Task.CompletedTask;
        }

        saga.Data.LedgerResends++;
        if (saga.Data.LedgerResends > _options.MaxLedgerResends)
        {
            saga.Data.Status = "UnderReview";
            saga.TransitionTo("NeedsReview", "The wallet service did not answer; an operator will check this transaction");
            saga.CancelWake();
            return Task.CompletedTask;
        }

        saga.Send(Command(saga));
        saga.WakeIn(TimeSpan.FromSeconds(_options.LedgerReplyTimeoutSeconds), LedgerCheck);
        return Task.CompletedTask;
    }

    private static bool Waiting(SagaContext<AgentTransactionData> saga) => saga.State is "Posting" or "NeedsReview";

    private static IntegrationEvent Command(SagaContext<AgentTransactionData> saga)
    {
        var data = saga.Data;
        return data.Kind switch
        {
            AgentTransactionKind.CashIn => new AgentCashIn(
                saga.Id, saga.Id, data.AgentOwnerUserId, data.CustomerWalletNumber!, data.AmountPaisa, data.CommissionPaisa, $"Cash deposit at {data.AgentName} ({data.AgentCode})"),
            AgentTransactionKind.CashOut => new AgentCashOut(
                saga.Id, saga.Id, data.AgentOwnerUserId, data.CustomerUserId!.Value, data.AmountPaisa, data.FeePaisa, data.TaxPaisa, data.CommissionPaisa,
                $"Cash withdrawal at {data.AgentName} ({data.AgentCode})", data.DeviceId),
            AgentTransactionKind.FloatIn => new MoveAgentFloat(saga.Id, saga.Id, data.AgentOwnerUserId, MoveAgentFloat.ToFloat, data.AmountPaisa),
            AgentTransactionKind.FloatOut => new MoveAgentFloat(saga.Id, saga.Id, data.AgentOwnerUserId, MoveAgentFloat.ToWallet, data.AmountPaisa),
            _ => throw new InvalidOperationException($"Unknown agent transaction kind '{data.Kind}'."),
        };
    }

    private static void Done(SagaContext<AgentTransactionData> saga, long agentBalance, DateTimeOffset postedAt, string detail)
    {
        saga.Data.AgentBalancePaisa = agentBalance;
        saga.Data.CompletedAt = postedAt;
        Finish(saga, "Completed", detail);
    }

    private static void Finish(SagaContext<AgentTransactionData> saga, string state, string detail)
    {
        var data = saga.Data;
        data.Status = state;
        saga.Complete(state, detail);
        saga.Send(new AgentTransactionChanged(saga.Id, data.AgentId, data.CustomerUserId, data.Kind, state, data.AmountPaisa, data.FeePaisa, data.CommissionPaisa, data.FailureCode, saga.Now));
    }

    internal static string Rupees(long paisa) => string.Create(CultureInfo.InvariantCulture, $"Rs. {paisa / 100m:#,0.##}");
}
