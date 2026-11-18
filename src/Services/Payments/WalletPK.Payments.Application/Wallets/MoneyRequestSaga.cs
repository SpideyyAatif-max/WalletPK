using WalletPK.Contracts.Ledger;
using WalletPK.Contracts.Payments;
using WalletPK.Payments.Application.BankTransfers;
using WalletPK.Payments.Domain;
using WalletPK.Sagas;

namespace WalletPK.Payments.Application.Wallets;

/// <summary>A money request's data (M6.2), stored as JSON in its saga_instances row.</summary>
public sealed class MoneyRequestData
{
    public Guid RequesterUserId { get; set; }

    public string RequesterWalletNumber { get; set; } = "";

    public string PayerWalletNumber { get; set; } = "";

    public long AmountPaisa { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The split bill this share belongs to.</summary>
    public Guid? SplitId { get; set; }

    public string? SplitTitle { get; set; }

    public long? SplitTotalPaisa { get; set; }

    /// <summary>The requester's own share of the split (not requested from anyone).</summary>
    public long? SplitOwnSharePaisa { get; set; }

    public Guid? PayerUserId { get; set; }

    public string? PayerDeviceId { get; set; }

    /// <summary>The current payment's transfer (journal) ID.</summary>
    public Guid? PaymentId { get; set; }

    public int PaymentAttempts { get; set; }

    public long FeePaisa { get; set; }

    public long TaxPaisa { get; set; }

    /// <summary>Why the last payment attempt failed (the request stays open).</summary>
    public string? LastFailureCode { get; set; }

    public string? LastFailureReason { get; set; }

    public string? DeclineReason { get; set; }

    public int LedgerResends { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>The payer pays (sent by the API once it has checked who is asking).</summary>
public sealed record PayMoneyRequest(Guid PayerUserId, string? DeviceId);

public sealed record DeclineMoneyRequest(string? Reason);

public sealed record CancelMoneyRequest;

/// <summary>
/// A request for money from another WalletPK customer (M6.2), and each share of a split bill.
/// <code>
/// Pending ──pay──▶ Paying ──FundsTransferred──▶ Paid
///   │  ▲            └──FundsTransferRejected──┘ (back to Pending, with the reason; the payer can try again)
///   ├──decline──▶ Declined
///   ├──cancel───▶ Cancelled
///   └──(expiry)─▶ Expired
/// </code>
/// Paying sends TransferFunds from the payer to the requester; each attempt has its own transfer ID
/// (request + attempt number), so a failed attempt can be followed by a new one, and none moves money twice.
/// </summary>
public sealed class MoneyRequestSaga : SagaDefinition<MoneyRequestData>
{
    public const string SagaName = "money-request";

    private const string Expire = "expire";
    private const string LedgerCheck = "ledger-check";

    private readonly BankTransferOptions _options;

    public MoneyRequestSaga(BankTransferOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        On<PayMoneyRequest>(OnPay);
        On<DeclineMoneyRequest>(OnDecline);
        On<CancelMoneyRequest>(OnCancel);
        On<FundsTransferred>(OnPaid);
        On<FundsTransferRejected>(OnPaymentRejected);
    }

    public override string Name => SagaName;

    /// <summary>The transfer ID of a request's <paramref name="attempt"/>-th payment.</summary>
    public static Guid PaymentIdFor(Guid requestId, int attempt) => SagaIds.From("money-request-payment", requestId, attempt);

    protected override void OnStart(SagaContext<MoneyRequestData> saga)
    {
        var data = saga.Data;
        var what = data.SplitTitle is null ? "" : $" for \"{data.SplitTitle}\"";
        saga.TransitionTo(MoneyRequestStates.Pending, $"{data.RequesterWalletNumber} asked {data.PayerWalletNumber} for {WalletMoney.Rupees(data.AmountPaisa)}{what}");
        Publish(saga, "Requested");
        saga.WakeAt(data.ExpiresAt, Expire);
    }

    private void OnPay(SagaContext<MoneyRequestData> saga, PayMoneyRequest pay)
    {
        if (saga.State != MoneyRequestStates.Pending)
        {
            return;
        }

        var data = saga.Data;
        data.PaymentAttempts++;
        data.PaymentId = PaymentIdFor(saga.Id, data.PaymentAttempts);
        data.PayerUserId = pay.PayerUserId;
        data.PayerDeviceId = pay.DeviceId;
        data.LastFailureCode = null;
        data.LastFailureReason = null;
        data.LedgerResends = 0;
        saga.TransitionTo(MoneyRequestStates.Paying, $"{data.PayerWalletNumber} is paying");
        saga.Send(Payment(saga));
        saga.WakeIn(TimeSpan.FromSeconds(_options.LedgerReplyTimeoutSeconds), LedgerCheck);
    }

    private static void OnDecline(SagaContext<MoneyRequestData> saga, DeclineMoneyRequest decline)
    {
        if (saga.State != MoneyRequestStates.Pending)
        {
            return;
        }

        saga.Data.DeclineReason = decline.Reason;
        Finish(saga, MoneyRequestStates.Declined, decline.Reason is null ? $"{saga.Data.PayerWalletNumber} declined" : $"{saga.Data.PayerWalletNumber} declined: {decline.Reason}");
    }

    private static void OnCancel(SagaContext<MoneyRequestData> saga, CancelMoneyRequest cancel)
    {
        if (saga.State == MoneyRequestStates.Pending)
        {
            Finish(saga, MoneyRequestStates.Cancelled, "Cancelled by the requester");
        }
    }

    private static void OnPaid(SagaContext<MoneyRequestData> saga, FundsTransferred reply)
    {
        var data = saga.Data;
        if (reply.TransferId != data.PaymentId || saga.State is not (MoneyRequestStates.Paying or MoneyRequestStates.NeedsReview))
        {
            return;
        }

        data.FeePaisa = reply.FeePaisa;
        data.TaxPaisa = reply.TaxPaisa;
        Finish(saga, MoneyRequestStates.Paid, $"{data.PayerWalletNumber} paid {WalletMoney.Rupees(reply.AmountPaisa)}");
    }

    private static void OnPaymentRejected(SagaContext<MoneyRequestData> saga, FundsTransferRejected reply)
    {
        var data = saga.Data;
        if (reply.TransferId != data.PaymentId || saga.State is not (MoneyRequestStates.Paying or MoneyRequestStates.NeedsReview))
        {
            return;
        }

        // The request stays open: the payer can top up and try again until it expires.
        data.LastFailureCode = reply.Code;
        data.LastFailureReason = reply.Reason;
        saga.TransitionTo(MoneyRequestStates.Pending, $"Payment failed: {reply.Reason}");
        Publish(saga, "Payment failed");
        saga.WakeAt(data.ExpiresAt > saga.Now ? data.ExpiresAt : saga.Now, Expire);
    }

    protected override Task OnWakeAsync(SagaContext<MoneyRequestData> saga, IServiceProvider services, CancellationToken cancellationToken)
    {
        var data = saga.Data;
        switch (saga.WakeReason, saga.State)
        {
            case (Expire, MoneyRequestStates.Pending) when saga.Now >= data.ExpiresAt:
                Finish(saga, MoneyRequestStates.Expired, "Expired without being paid");
                break;
            case (Expire, MoneyRequestStates.Pending):
                saga.WakeAt(data.ExpiresAt, Expire);
                break;
            case (LedgerCheck, MoneyRequestStates.Paying):
                data.LedgerResends++;
                if (data.LedgerResends > _options.MaxLedgerResends)
                {
                    saga.TransitionTo(MoneyRequestStates.NeedsReview, "The wallet service did not answer; an operator will check this payment");
                    saga.CancelWake();
                    break;
                }

                saga.Send(Payment(saga));
                saga.WakeIn(TimeSpan.FromSeconds(_options.LedgerReplyTimeoutSeconds), LedgerCheck);
                break;
            default:
                break;
        }

        return Task.CompletedTask;
    }

    private static TransferFunds Payment(SagaContext<MoneyRequestData> saga) =>
        new(saga.Data.PaymentId!.Value, saga.Id, saga.Data.PayerUserId!.Value, saga.Data.RequesterWalletNumber, saga.Data.AmountPaisa, saga.Data.PayerDeviceId);

    private static void Finish(SagaContext<MoneyRequestData> saga, string state, string detail)
    {
        saga.Data.FinishedAt = saga.Now;
        saga.Complete(state, detail);
        Publish(saga, detail);
    }

    private static void Publish(SagaContext<MoneyRequestData> saga, string detail)
    {
        var data = saga.Data;
        saga.Send(new MoneyRequestChanged(
            saga.Id, data.RequesterUserId, data.RequesterWalletNumber, data.PayerWalletNumber, saga.State, data.AmountPaisa, data.SplitId, detail, saga.Now));
    }
}
