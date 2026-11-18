using WalletPK.Contracts.Ledger;
using WalletPK.Contracts.TopupMerchant;
using WalletPK.Sagas;

namespace WalletPK.TopupMerchant.Application.Merchants;

/// <summary>A QR payment's data, stored as JSON in its saga_instances row.</summary>
public sealed class MerchantPaymentData
{
    public Guid PayerUserId { get; set; }

    public string? PayerWalletNumber { get; set; }

    public string? DeviceId { get; set; }

    public string IdempotencyKey { get; set; } = "";

    public Guid MerchantId { get; set; }

    public Guid MerchantOwnerUserId { get; set; }

    public string MerchantCode { get; set; } = "";

    public string MerchantName { get; set; } = "";

    public Guid QrId { get; set; }

    public string? Reference { get; set; }

    public long AmountPaisa { get; set; }

    public long FeePaisa { get; set; }

    public long TaxPaisa { get; set; }

    public long NetPaisa { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Processing, Completed or Failed.</summary>
    public string Status { get; set; } = "Processing";

    public DateTimeOffset? PaidAt { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureReason { get; set; }

    public int LedgerResends { get; set; }
}

/// <summary>QR payment settings, from "Merchants:Payments".</summary>
public sealed class MerchantPaymentOptions
{
    public const string SectionName = "Merchants:Payments";

    public int LedgerReplyTimeoutSeconds { get; set; } = 30;

    public int MaxLedgerResends { get; set; } = 60;

    public bool IsValid() => LedgerReplyTimeoutSeconds >= 1 && MaxLedgerResends >= 1;
}

/// <summary>
/// A QR payment (M8.4): one PayMerchant command, and the Ledger moves the money in one journal with the
/// merchant's fee and its tax split off. The payment ID is the journal ID and refusals are remembered, so
/// the command is sent again whenever no answer comes, and never pays twice or after a "no".
/// <code>Paying ──MerchantPaid──▶ Completed
///    └──FundsTransferRejected──▶ Failed (the QR code can be paid again)</code>
/// </summary>
public sealed class MerchantPaymentSaga : SagaDefinition<MerchantPaymentData>
{
    public const string SagaName = "merchant-payment";

    private const string LedgerCheck = "ledger-check";

    private readonly MerchantPaymentOptions _options;

    public MerchantPaymentSaga(MerchantPaymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        On<MerchantPaid>((saga, reply) =>
        {
            if (saga.State is not ("Paying" or "NeedsReview"))
            {
                return;
            }

            saga.Data.PaidAt = reply.PostedAt;
            Finish(saga, "Completed", $"Paid {Money(reply.AmountPaisa)} to {saga.Data.MerchantName}");
        });
        On<FundsTransferRejected>((saga, reply) =>
        {
            if (saga.State is not ("Paying" or "NeedsReview"))
            {
                return;
            }

            saga.Data.FailureCode = reply.Code;
            saga.Data.FailureReason = reply.Reason;
            Finish(saga, "Failed", $"Not paid: {reply.Reason}");
        });
    }

    public override string Name => SagaName;

    protected override void OnStart(SagaContext<MerchantPaymentData> saga)
    {
        saga.TransitionTo("Paying", $"Paying {Money(saga.Data.AmountPaisa)} to {saga.Data.MerchantName}{(saga.Data.Reference is null ? "" : $" ({saga.Data.Reference})")}");
        saga.Send(Command(saga));
        saga.WakeIn(TimeSpan.FromSeconds(_options.LedgerReplyTimeoutSeconds), LedgerCheck);
    }

    protected override Task OnWakeAsync(SagaContext<MerchantPaymentData> saga, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (saga.WakeReason != LedgerCheck || saga.State != "Paying")
        {
            return Task.CompletedTask;
        }

        saga.Data.LedgerResends++;
        if (saga.Data.LedgerResends > _options.MaxLedgerResends)
        {
            saga.TransitionTo("NeedsReview", "The wallet service did not answer; an operator will check this payment");
            saga.CancelWake();
            return Task.CompletedTask;
        }

        saga.Send(Command(saga));
        saga.WakeIn(TimeSpan.FromSeconds(_options.LedgerReplyTimeoutSeconds), LedgerCheck);
        return Task.CompletedTask;
    }

    private static PayMerchant Command(SagaContext<MerchantPaymentData> saga)
    {
        var data = saga.Data;
        return new PayMerchant(
            saga.Id, saga.Id, data.PayerUserId, data.MerchantOwnerUserId, data.AmountPaisa, data.FeePaisa, data.TaxPaisa,
            $"QR payment to {data.MerchantName}{(data.Reference is null ? "" : $" ({data.Reference})")}", data.DeviceId);
    }

    private static void Finish(SagaContext<MerchantPaymentData> saga, string state, string detail)
    {
        var data = saga.Data;
        data.Status = state;
        saga.Complete(state, detail);
        saga.Send(new MerchantPaymentChanged(saga.Id, data.MerchantId, data.PayerUserId, data.QrId, state, data.AmountPaisa, data.FeePaisa, data.TaxPaisa, data.NetPaisa, data.Reference, saga.Now));
    }

    internal static string Money(long paisa) => TopUps.TopUpSaga.Rupees(paisa);
}
