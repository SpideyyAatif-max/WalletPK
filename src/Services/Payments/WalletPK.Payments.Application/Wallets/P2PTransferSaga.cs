using System.Globalization;
using WalletPK.Common.Primitives;
using WalletPK.Contracts.Ledger;
using WalletPK.Contracts.Payments;
using WalletPK.Payments.Application.BankTransfers;
using WalletPK.Payments.Domain;
using WalletPK.Sagas;

namespace WalletPK.Payments.Application.Wallets;

/// <summary>A wallet-to-wallet transfer's data, stored as JSON in its saga_instances row.</summary>
public sealed class P2PTransferData
{
    public Guid SenderUserId { get; set; }

    public string SenderWalletNumber { get; set; } = "";

    public string IdempotencyKey { get; set; } = "";

    public string ToWalletNumber { get; set; } = "";

    public long AmountPaisa { get; set; }

    public string? Note { get; set; }

    public string? DeviceId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Processing, Completed or Failed.</summary>
    public string Status { get; set; } = "Processing";

    public long FeePaisa { get; set; }

    public long TaxPaisa { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureReason { get; set; }

    public int LedgerResends { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// A wallet-to-wallet transfer through Payments (M6.1): one TransferFunds command, and the Ledger moves
/// the money in one journal (instant, internal ledger only), answering FundsTransferred or
/// FundsTransferRejected. The transfer ID is the journal ID and the Ledger remembers refusals, so the
/// command can be sent again whenever no answer comes, and it never moves money twice or after a "no".
/// <code>Sending ──FundsTransferred──▶ Completed
///    └────FundsTransferRejected──▶ Failed</code>
/// </summary>
public sealed class P2PTransferSaga : SagaDefinition<P2PTransferData>
{
    public const string SagaName = "p2p-transfer";

    private const string LedgerCheck = "ledger-check";

    private readonly BankTransferOptions _options;

    public P2PTransferSaga(BankTransferOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        On<FundsTransferred>(OnTransferred);
        On<FundsTransferRejected>(OnRejected);
    }

    public override string Name => SagaName;

    protected override void OnStart(SagaContext<P2PTransferData> saga)
    {
        var data = saga.Data;
        saga.TransitionTo(P2PTransferStates.Sending, $"Sending {WalletMoney.Rupees(data.AmountPaisa)} to {data.ToWalletNumber}");
        saga.Send(Command(saga));
        saga.WakeIn(TimeSpan.FromSeconds(_options.LedgerReplyTimeoutSeconds), LedgerCheck);
    }

    private void OnTransferred(SagaContext<P2PTransferData> saga, FundsTransferred reply)
    {
        if (saga.State != P2PTransferStates.Sending && saga.State != P2PTransferStates.NeedsReview)
        {
            return;
        }

        var data = saga.Data;
        data.FeePaisa = reply.FeePaisa;
        data.TaxPaisa = reply.TaxPaisa;
        Finish(saga, P2PTransferStates.Completed, "Completed", $"{WalletMoney.Rupees(reply.AmountPaisa)} sent to {data.ToWalletNumber}");
    }

    private void OnRejected(SagaContext<P2PTransferData> saga, FundsTransferRejected reply)
    {
        if (saga.State != P2PTransferStates.Sending && saga.State != P2PTransferStates.NeedsReview)
        {
            return;
        }

        saga.Data.FailureCode = reply.Code;
        saga.Data.FailureReason = reply.Reason;
        Finish(saga, P2PTransferStates.Failed, "Failed", $"Not sent: {reply.Reason}");
    }

    protected override Task OnWakeAsync(SagaContext<P2PTransferData> saga, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (saga.WakeReason != LedgerCheck || saga.State != P2PTransferStates.Sending)
        {
            return Task.CompletedTask;
        }

        // No answer yet: ask again (same transfer ID, so at most one journal).
        saga.Data.LedgerResends++;
        if (saga.Data.LedgerResends > _options.MaxLedgerResends)
        {
            saga.TransitionTo(P2PTransferStates.NeedsReview, "The wallet service did not answer; an operator will check this transfer");
            saga.CancelWake();
            return Task.CompletedTask;
        }

        saga.Send(Command(saga));
        saga.WakeIn(TimeSpan.FromSeconds(_options.LedgerReplyTimeoutSeconds), LedgerCheck);
        return Task.CompletedTask;
    }

    private static TransferFunds Command(SagaContext<P2PTransferData> saga) =>
        new(saga.Id, saga.Id, saga.Data.SenderUserId, saga.Data.ToWalletNumber, saga.Data.AmountPaisa, saga.Data.DeviceId);

    private static void Finish(SagaContext<P2PTransferData> saga, string state, string status, string detail)
    {
        var data = saga.Data;
        data.Status = status;
        data.FinishedAt = saga.Now;
        saga.Complete(state, detail);
        saga.Send(new P2PTransferChanged(saga.Id, data.SenderUserId, data.ToWalletNumber, status, data.AmountPaisa, detail, saga.Now));
    }
}

/// <summary>Money for people.</summary>
public static class WalletMoney
{
    public static string Rupees(long paisa) =>
        string.Create(CultureInfo.InvariantCulture, $"Rs. {paisa / 100m:#,0.##}");

    /// <summary>"+923001234567" → "+92300*****67", for people who should not see the whole number.</summary>
    public static string Mask(string walletNumber) => PakistaniMobileNumber.Mask(walletNumber);
}
