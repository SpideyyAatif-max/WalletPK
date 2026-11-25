using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Bills.Application.Billers;
using WalletPK.Bills.Domain;
using WalletPK.Contracts.Bills;
using WalletPK.Contracts.Ledger;
using WalletPK.Sagas;

namespace WalletPK.Bills.Application.Payments;

/// <summary>A bill payment's data, stored as JSON in its saga_instances row.</summary>
public sealed class BillPaymentData
{
    public Guid UserId { get; set; }

    public string? DeviceId { get; set; }

    public string IdempotencyKey { get; set; } = "";

    public string Biller { get; set; } = "";

    public string ConsumerNumber { get; set; } = "";

    public string? CustomerName { get; set; }

    public long AmountPaisa { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>What the customer sees (<see cref="BillPaymentStatus"/>).</summary>
    public string Status { get; set; } = BillPaymentStatus.Processing;

    public string? BillerReference { get; set; }

    public string? BillingMonth { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>Calls to the biller's payment API (each with the same payment ID).</summary>
    public int PayAttempts { get; set; }

    /// <summary>When the biller was first found unavailable.</summary>
    public DateTimeOffset? RetryingSince { get; set; }

    /// <summary>When the outcome became unknown (the start of verification).</summary>
    public DateTimeOffset? VerifyingSince { get; set; }

    public int StatusChecks { get; set; }

    public int LedgerResends { get; set; }

    public string? DeadLetterReason { get; set; }

    public DateTimeOffset? DeadLetteredAt { get; set; }

    /// <summary>The operator who resolved it, and how.</summary>
    public string? ResolvedBy { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Operations (M7.5): check the biller again now.</summary>
public sealed record ReplayBillPayment(string Operator);

/// <summary>Operations: the biller was not paid (checked in its settlement file): return the money.</summary>
public sealed record RefundBillPayment(string Operator, string Reason);

/// <summary>Operations: the biller was paid (its settlement file shows it): settle the reserved money.</summary>
public sealed record ConfirmBillPayment(string Operator, string BillerReference);

/// <summary>Bill payment settings, from "Bills:Payments".</summary>
public sealed class BillPaymentOptions
{
    public const string SectionName = "Bills:Payments";

    /// <summary>The reserved money returns by itself after this long (Ledger maximum: 7 days).</summary>
    public int HoldLifetimeHours { get; set; } = 168;

    public int LedgerReplyTimeoutSeconds { get; set; } = 60;

    /// <summary>After this many unanswered resends the payment is dead-lettered.</summary>
    public int MaxLedgerResends { get; set; } = 60;

    /// <summary>Calls to the biller's payment API before giving up (each attempt with the same payment ID).</summary>
    public int MaxPayAttempts { get; set; } = 5;

    /// <summary>First retry while the biller is unavailable; doubles up to <see cref="RetryMaxSeconds"/>.</summary>
    public int RetryInitialSeconds { get; set; } = 10;

    public int RetryMaxSeconds { get; set; } = 120;

    /// <summary>A biller unavailable for this long: the payment is given up and the money returned.</summary>
    public int MaxRetryMinutes { get; set; } = 30;

    /// <summary>First status query after an unknown outcome; doubles up to <see cref="VerifyMaxSeconds"/>.</summary>
    public int VerifyDelaySeconds { get; set; } = 5;

    public int VerifyMaxSeconds { get; set; } = 60;

    /// <summary>Quick checks for this long, then the payment is pending verification (M7.4).</summary>
    public int FastVerifyMinutes { get; set; } = 10;

    /// <summary>How often a payment pending verification is checked.</summary>
    public int PendingCheckSeconds { get; set; } = 600;

    /// <summary>Pending verification this long: the payment is dead-lettered for a person (M7.5).</summary>
    public int MaxPendingHours { get; set; } = 48;

    public bool IsValid() =>
        HoldLifetimeHours is >= 1 and <= 168
        && LedgerReplyTimeoutSeconds >= 1 && MaxLedgerResends >= 1
        && MaxPayAttempts >= 1
        && RetryInitialSeconds >= 1 && RetryMaxSeconds >= RetryInitialSeconds && MaxRetryMinutes >= 1
        && VerifyDelaySeconds >= 1 && VerifyMaxSeconds >= VerifyDelaySeconds
        && FastVerifyMinutes >= 1 && PendingCheckSeconds >= 1 && MaxPendingHours >= 1
        // The reserved money must outlive every automatic step, leaving time for a person.
        && HoldLifetimeHours * 60 > MaxRetryMinutes + FastVerifyMinutes + (MaxPendingHours * 60);
}

/// <summary>
/// Paying a bill (M7.3). The money is reserved, the biller is paid, and only then does the money leave
/// the wallet (into BILLER_SETTLEMENT, which pays the biller in the daily settlement).
/// <code>
/// Reserving ─HoldPlaced─▶ Paying ─ok─▶ Settling ─HoldCaptured─▶ Completed
///    │                     │ │ └─refused (4xx)─▶ Returning ─HoldReleased─▶ Failed
///    │ HoldRejected        │ └─unavailable─▶ Retrying ─(back-off)─▶ Paying   (gives up after 30 min → Returning)
///    ▼                     └─timeout─▶ Verifying ─status query─▶ paid: Settling / never arrived: Paying / no answer:
///  Failed                                └─after 10 min─▶ PendingVerification ─(every 10 min)─▶ … ─after 48 h─▶ DeadLettered
/// </code>
/// A timeout is never retried blindly: the biller is asked first whether the payment arrived, so a bill is
/// never paid twice. Only a confirmed "not paid" returns the money. Unresolved payments are dead-lettered
/// (topic bills.dlq) for a person, who replays, refunds or confirms them (M7.5).
/// </summary>
public sealed class BillPaymentSaga : SagaDefinition<BillPaymentData>
{
    public const string SagaName = "bill-payment";

    public const string JournalPurpose = "BillPayment";

    public const string BillerSettlementAccount = "BILLER_SETTLEMENT";

    private const string Pay = "pay";
    private const string Verify = "verify";
    private const string LedgerCheck = "ledger-check";

    private readonly BillPaymentOptions _options;

    public BillPaymentSaga(BillPaymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        On<HoldPlaced>(OnHoldPlaced);
        On<HoldRejected>(OnHoldRejected);
        On<HoldCaptured>(OnHoldCaptured);
        On<HoldCaptureFailed>(OnHoldCaptureFailed);
        On<HoldReleased>(OnHoldReleased);
        On<ReplayBillPayment>(OnReplay);
        On<RefundBillPayment>(OnRefund);
        On<ConfirmBillPayment>(OnConfirm);
    }

    public override string Name => SagaName;

    protected override void OnStart(SagaContext<BillPaymentData> saga)
    {
        var data = saga.Data;
        saga.TransitionTo(BillPaymentStates.Reserving, $"{Rupees(data.AmountPaisa)} {BillerOf(data).Name} bill for {data.ConsumerNumber} requested");
        saga.Send(PlaceHoldFor(saga));
        Publish(saga, "Reserving the money in your wallet");
        saga.WakeIn(LedgerTimeout, LedgerCheck);
    }

    // ---- Ledger replies ----

    private void OnHoldPlaced(SagaContext<BillPaymentData> saga, HoldPlaced reply)
    {
        if (saga.State != BillPaymentStates.Reserving)
        {
            return;
        }

        saga.Data.LedgerResends = 0;
        saga.TransitionTo(BillPaymentStates.Paying, $"{Rupees(reply.AmountPaisa)} reserved in your wallet");
        saga.WakeNow(Pay);
    }

    private static void OnHoldRejected(SagaContext<BillPaymentData> saga, HoldRejected reply)
    {
        if (saga.State != BillPaymentStates.Reserving)
        {
            return;
        }

        saga.Data.FailureCode = reply.Code;
        saga.Data.FailureReason = reply.Reason;
        Finish(saga, BillPaymentStates.Failed, BillPaymentStatus.Failed, $"Not paid: {reply.Reason}");
    }

    private static void OnHoldCaptured(SagaContext<BillPaymentData> saga, HoldCaptured reply)
    {
        if (saga.State != BillPaymentStates.Settling)
        {
            return;
        }

        var data = saga.Data;
        Finish(saga, BillPaymentStates.Completed, BillPaymentStatus.Completed, $"{BillerOf(data).Name} bill {data.BillingMonth} paid (reference {data.BillerReference})");
    }

    private void OnHoldCaptureFailed(SagaContext<BillPaymentData> saga, HoldCaptureFailed reply)
    {
        if (saga.State == BillPaymentStates.Settling)
        {
            // The biller has the money but the wallet could not be settled: a person must look.
            DeadLetter(saga, $"The biller was paid but settling the wallet failed ({reply.Code}: {reply.Reason})");
        }
    }

    private static void OnHoldReleased(SagaContext<BillPaymentData> saga, HoldReleased reply)
    {
        if (saga.State != BillPaymentStates.Returning)
        {
            return;
        }

        var detail = reply.Status == "NotPlaced"
            ? $"Not paid: {saga.Data.FailureReason}"
            : $"Not paid: {saga.Data.FailureReason}. The reserved money is back in your wallet";
        Finish(saga, BillPaymentStates.Failed, BillPaymentStatus.Failed, detail);
    }

    // ---- Operations (M7.5) ----

    private void OnReplay(SagaContext<BillPaymentData> saga, ReplayBillPayment replay)
    {
        if (saga.State is not (BillPaymentStates.DeadLettered or BillPaymentStates.PendingVerification))
        {
            return;
        }

        var data = saga.Data;
        data.VerifyingSince = saga.Now;
        data.StatusChecks = 0;
        saga.TransitionTo(BillPaymentStates.Verifying, $"Replayed by {replay.Operator}: asking {BillerOf(data).Name} again");
        Publish(saga, "Checking with the biller again", BillPaymentStatus.PendingVerification);
        saga.WakeNow(Verify);
    }

    private void OnRefund(SagaContext<BillPaymentData> saga, RefundBillPayment refund)
    {
        if (saga.State is not (BillPaymentStates.DeadLettered or BillPaymentStates.PendingVerification))
        {
            return;
        }

        saga.Data.ResolvedBy = $"{refund.Operator}: refund";
        StartReturning(saga, "bills.refunded_by_operator", $"Refunded by our team ({refund.Reason})");
    }

    private void OnConfirm(SagaContext<BillPaymentData> saga, ConfirmBillPayment confirm)
    {
        if (saga.State is not (BillPaymentStates.DeadLettered or BillPaymentStates.PendingVerification))
        {
            return;
        }

        saga.Data.ResolvedBy = $"{confirm.Operator}: confirmed";
        StartSettling(saga, confirm.BillerReference, saga.Data.BillingMonth, $"Confirmed paid by {confirm.Operator} (biller reference {confirm.BillerReference})");
    }

    // ---- Timer steps (I/O allowed) ----

    protected override async Task OnWakeAsync(SagaContext<BillPaymentData> saga, IServiceProvider services, CancellationToken cancellationToken)
    {
        switch (saga.WakeReason, saga.State)
        {
            case (Pay, BillPaymentStates.Paying or BillPaymentStates.Retrying):
                await PayAsync(saga, services.GetRequiredService<IBillerGateway>(), cancellationToken);
                break;
            case (Verify, BillPaymentStates.Verifying or BillPaymentStates.PendingVerification):
                await VerifyAsync(saga, services.GetRequiredService<IBillerGateway>(), cancellationToken);
                break;
            case (LedgerCheck, BillPaymentStates.Reserving or BillPaymentStates.Settling or BillPaymentStates.Returning):
                ResendLedgerCommand(saga);
                break;
            default:
                break;
        }
    }

    private async Task PayAsync(SagaContext<BillPaymentData> saga, IBillerGateway billers, CancellationToken cancellationToken)
    {
        var data = saga.Data;
        var biller = BillerOf(data);
        data.PayAttempts++;
        var result = await billers.PayAsync(biller, saga.Id, data.ConsumerNumber, data.AmountPaisa, cancellationToken);

        switch (result.Outcome)
        {
            case BillerCallOutcome.Ok:
                StartSettling(saga, result.Receipt!.BillerReference, result.Receipt.BillingMonth, $"{biller.Name} accepted the payment (reference {result.Receipt.BillerReference})");
                break;

            case BillerCallOutcome.Unavailable:
                data.RetryingSince ??= saga.Now;
                if (data.PayAttempts >= _options.MaxPayAttempts || saga.Now - data.RetryingSince.Value >= TimeSpan.FromMinutes(_options.MaxRetryMinutes))
                {
                    StartReturning(saga, "bills.biller_unavailable", $"{biller.Name} was unavailable ({result.Detail})");
                    break;
                }

                if (saga.State != BillPaymentStates.Retrying)
                {
                    saga.TransitionTo(BillPaymentStates.Retrying, $"{biller.Name} is unavailable ({result.Detail}); trying again shortly");
                }

                saga.WakeIn(Backoff(_options.RetryInitialSeconds, _options.RetryMaxSeconds, data.PayAttempts), Pay);
                break;

            case BillerCallOutcome.Unknown:
                // Never pay again blindly: first ask whether this payment arrived.
                data.VerifyingSince = saga.Now;
                data.StatusChecks = 0;
                saga.TransitionTo(BillPaymentStates.Verifying, $"No answer from {biller.Name} ({result.Detail}); checking whether the payment arrived");
                saga.WakeIn(TimeSpan.FromSeconds(_options.VerifyDelaySeconds), Verify);
                break;

            default:
                // Refused (already paid, disconnected, wrong amount) or no such consumer: certainly not paid.
                StartReturning(saga, result.Code ?? "bills.refused", $"{biller.Name} refused it ({result.Detail})");
                break;
        }
    }

    private async Task VerifyAsync(SagaContext<BillPaymentData> saga, IBillerGateway billers, CancellationToken cancellationToken)
    {
        var data = saga.Data;
        var biller = BillerOf(data);
        data.StatusChecks++;
        var result = await billers.GetPaymentAsync(biller, saga.Id, cancellationToken);

        if (result.Outcome == BillerCallOutcome.Ok)
        {
            StartSettling(saga, result.Receipt!.BillerReference, result.Receipt.BillingMonth, $"{biller.Name} confirms the payment arrived (reference {result.Receipt.BillerReference})");
            return;
        }

        if (result.Outcome == BillerCallOutcome.NotFound)
        {
            // The biller never received it: paying (again) cannot pay twice.
            if (data.PayAttempts >= _options.MaxPayAttempts)
            {
                StartReturning(saga, "bills.biller_unreachable", $"{biller.Name} never received the payment after {data.PayAttempts} attempts");
                return;
            }

            if (data.Status != BillPaymentStatus.Processing)
            {
                Publish(saga, "Paying the biller again", BillPaymentStatus.Processing);
            }

            saga.TransitionTo(BillPaymentStates.Paying, $"{biller.Name} never received it; paying again");
            saga.WakeNow(Pay);
            return;
        }

        // No answer to the question either.
        var waited = saga.Now - (data.VerifyingSince ?? saga.Now);
        if (waited >= TimeSpan.FromHours(_options.MaxPendingHours) + TimeSpan.FromMinutes(_options.FastVerifyMinutes))
        {
            DeadLetter(saga, $"{biller.Name} has not confirmed or denied the payment for {_options.MaxPendingHours} hours");
            return;
        }

        if (saga.State == BillPaymentStates.Verifying && waited >= TimeSpan.FromMinutes(_options.FastVerifyMinutes))
        {
            saga.TransitionTo(BillPaymentStates.PendingVerification, $"{biller.Name} has not confirmed yet ({result.Detail}); the money stays reserved until it does");
            Publish(saga, $"Waiting for {biller.Name} to confirm; your money stays reserved", BillPaymentStatus.PendingVerification);
        }

        saga.WakeIn(
            saga.State == BillPaymentStates.PendingVerification
                ? TimeSpan.FromSeconds(_options.PendingCheckSeconds)
                : Backoff(_options.VerifyDelaySeconds, _options.VerifyMaxSeconds, data.StatusChecks),
            Verify);
    }

    private void ResendLedgerCommand(SagaContext<BillPaymentData> saga)
    {
        var data = saga.Data;
        data.LedgerResends++;
        if (data.LedgerResends > _options.MaxLedgerResends)
        {
            if (saga.State == BillPaymentStates.Reserving)
            {
                StartReturning(saga, "bills.ledger_unavailable", "The wallet service did not answer");
            }
            else
            {
                DeadLetter(saga, $"The wallet service did not answer while {(saga.State == BillPaymentStates.Settling ? "settling" : "returning")} the money");
            }

            return;
        }

        saga.Send(saga.State switch
        {
            BillPaymentStates.Reserving => PlaceHoldFor(saga),
            BillPaymentStates.Settling => CaptureHoldFor(saga),
            _ => new ReleaseHold(saga.Id, data.FailureReason ?? "Bill payment cancelled"),
        });
        saga.WakeIn(LedgerTimeout, LedgerCheck);
    }

    // ---- Moves ----

    private void StartSettling(SagaContext<BillPaymentData> saga, string reference, string? billingMonth, string detail)
    {
        var data = saga.Data;
        data.BillerReference = reference;
        data.BillingMonth = billingMonth ?? data.BillingMonth;
        data.LedgerResends = 0;
        saga.TransitionTo(BillPaymentStates.Settling, detail);
        saga.Send(CaptureHoldFor(saga));
        saga.WakeIn(LedgerTimeout, LedgerCheck);
    }

    private void StartReturning(SagaContext<BillPaymentData> saga, string code, string reason)
    {
        var data = saga.Data;
        data.FailureCode = code;
        data.FailureReason = reason;
        data.LedgerResends = 0;
        saga.TransitionTo(BillPaymentStates.Returning, $"{reason}; returning the reserved money to your wallet");
        saga.Send(new ReleaseHold(saga.Id, reason));
        saga.WakeIn(LedgerTimeout, LedgerCheck);
    }

    private static void DeadLetter(SagaContext<BillPaymentData> saga, string reason)
    {
        var data = saga.Data;
        var lastState = saga.State;
        data.DeadLetterReason = reason;
        data.DeadLetteredAt = saga.Now;
        saga.TransitionTo(BillPaymentStates.DeadLettered, $"{reason}; our team will resolve it (the money stays reserved)");
        saga.CancelWake();
        saga.Send(new BillPaymentDeadLettered(saga.Id, data.UserId, data.Biller, data.ConsumerNumber, data.AmountPaisa, lastState, reason, saga.Now));
        Publish(saga, "Our team is checking this payment; your money stays reserved", BillPaymentStatus.UnderReview);
    }

    private static void Finish(SagaContext<BillPaymentData> saga, string state, string status, string detail)
    {
        saga.Data.FinishedAt = saga.Now;
        saga.Data.Status = status;
        saga.Complete(state, detail);
        Publish(saga, detail, status);
    }

    // ---- Messages ----

    private PlaceHold PlaceHoldFor(SagaContext<BillPaymentData> saga)
    {
        var data = saga.Data;
        return new PlaceHold(
            saga.Id,
            data.UserId,
            data.AmountPaisa,
            JournalPurpose,
            $"{BillerOf(data).Name} bill {data.ConsumerNumber}",
            saga.Id.ToString(),
            (int)TimeSpan.FromHours(_options.HoldLifetimeHours).TotalSeconds,
            data.DeviceId);
    }

    private static CaptureHold CaptureHoldFor(SagaContext<BillPaymentData> saga)
    {
        var data = saga.Data;
        return new CaptureHold(
            saga.Id,
            saga.Id,
            JournalPurpose,
            $"{BillerOf(data).Name} bill {data.ConsumerNumber} {data.BillingMonth} ({data.BillerReference})",
            [new HoldCredit(BillerSettlementAccount, data.AmountPaisa)]);
    }

    private static void Publish(SagaContext<BillPaymentData> saga, string detail, string? status = null)
    {
        var data = saga.Data;
        data.Status = status ?? data.Status;
        saga.Send(new BillPaymentChanged(saga.Id, data.UserId, data.Biller, data.ConsumerNumber, data.Status, saga.State, data.AmountPaisa, detail, saga.Now, data.BillerReference));
    }

    // ---- Helpers ----

    private TimeSpan LedgerTimeout => TimeSpan.FromSeconds(_options.LedgerReplyTimeoutSeconds);

    internal static TimeSpan Backoff(int firstSeconds, int maxSeconds, int attempt) =>
        TimeSpan.FromSeconds(Math.Min(maxSeconds, firstSeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 20))));

    private static Biller BillerOf(BillPaymentData data) =>
        BillerCatalog.ByCode(data.Biller) ?? throw new InvalidOperationException($"Unknown biller '{data.Biller}'.");

    internal static string Rupees(long paisa) => string.Create(CultureInfo.InvariantCulture, $"Rs. {paisa / 100m:#,0.##}");
}
