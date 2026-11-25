using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Contracts.Ledger;
using WalletPK.Contracts.Payments;
using WalletPK.Payments.Application.Raast;
using WalletPK.Payments.Domain;
using WalletPK.Sagas;

namespace WalletPK.Payments.Application.BankTransfers;

/// <summary>
/// A Raast transfer from a wallet to a bank account (M6.4). The money is reserved first, then sent;
/// it leaves the wallet only once the bank has it, and comes back if the transfer cannot be made.
/// <code>
/// Reserving ──HoldPlaced──▶ Sending ──202──▶ AwaitingBank ──Completed──▶ Settling ──HoldCaptured──▶ Completed
///     │                      │ │ │               │                         │
///     │ HoldRejected         │ │ └─ 503 / circuit open ─▶ Queued ─(retry)─▶ Sending (gives up after 24 h)
///     ▼                      │ └─ timeout ─▶ Verifying ─(status query)─▶ AwaitingBank / Sending / Settling / Returning
///   Failed                   └─ 4xx ─▶ Returning ──HoldReleased──▶ Failed       Rejected ─┘
/// </code>
/// The hold ID, the journal ID and the transfer ID Raast sees are all the Saga ID, so every step can be
/// repeated safely (after a crash, a lost reply, a timeout). Calls to Raast happen in timer steps,
/// outside database transactions; replies and callbacks are messages.
/// </summary>
public sealed class BankTransferSaga : SagaDefinition<BankTransferData>
{
    public const string SagaName = "bank-transfer";

    /// <summary>The journal type the Ledger posts (and checks limits for).</summary>
    public const string JournalPurpose = "BankTransfer";

    /// <summary>Where money sent out through banks is credited (Raast transfers are free: no fee line).</summary>
    public const string BankFloatAccount = "BANK_FLOAT";

    private const string Send = "send";
    private const string Verify = "verify";
    private const string StatusCheck = "status-check";
    private const string LedgerCheck = "ledger-check";

    private readonly BankTransferOptions _options;

    public BankTransferSaga(BankTransferOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        On<HoldPlaced>(OnHoldPlaced);
        On<HoldRejected>(OnHoldRejected);
        On<HoldCaptured>(OnHoldCaptured);
        On<HoldCaptureFailed>(OnHoldCaptureFailed);
        On<HoldReleased>(OnHoldReleased);
        On<RaastTransferCallback>(OnBankAnswer);
    }

    public override string Name => SagaName;

    protected override void OnStart(SagaContext<BankTransferData> saga)
    {
        var data = saga.Data;
        saga.TransitionTo(BankTransferStates.Reserving, $"Transfer of {Rupees(data.AmountPaisa)} to {Beneficiary(data)} requested");
        saga.Send(PlaceHoldFor(saga));
        Publish(saga, "Reserving the money in your wallet");
        saga.WakeIn(LedgerReplyTimeout, LedgerCheck);
    }

    // ---- Ledger replies (messages: no I/O here) ----

    private void OnHoldPlaced(SagaContext<BankTransferData> saga, HoldPlaced reply)
    {
        if (saga.State != BankTransferStates.Reserving)
        {
            return; // A repeated reply.
        }

        saga.Data.LedgerResends = 0;
        saga.TransitionTo(BankTransferStates.Sending, $"{Rupees(reply.AmountPaisa)} reserved in your wallet");
        saga.WakeNow(Send);
    }

    private void OnHoldRejected(SagaContext<BankTransferData> saga, HoldRejected reply)
    {
        if (saga.State != BankTransferStates.Reserving)
        {
            return;
        }

        saga.Data.FailureCode = reply.Code;
        saga.Data.FailureReason = reply.Reason;
        Finish(saga, BankTransferStates.Failed, BankTransferStatus.Failed, $"Not sent: {reply.Reason}");
    }

    private void OnHoldCaptured(SagaContext<BankTransferData> saga, HoldCaptured reply)
    {
        if (saga.State != BankTransferStates.Settling)
        {
            return;
        }

        Finish(saga, BankTransferStates.Completed, BankTransferStatus.Completed, $"{Rupees(reply.AmountPaisa)} delivered to {Beneficiary(saga.Data)}");
    }

    private void OnHoldCaptureFailed(SagaContext<BankTransferData> saga, HoldCaptureFailed reply)
    {
        if (saga.State != BankTransferStates.Settling)
        {
            return;
        }

        // The bank paid out but the wallet could not be debited: only a person can sort this out.
        saga.Data.FailureCode = reply.Code;
        saga.Data.FailureReason = reply.Reason;
        Review(saga, $"The bank paid out but settling the wallet failed ({reply.Code}: {reply.Reason})");
    }

    private void OnHoldReleased(SagaContext<BankTransferData> saga, HoldReleased reply)
    {
        if (saga.State != BankTransferStates.Returning)
        {
            return;
        }

        var detail = reply.Status == "NotPlaced"
            ? $"Not sent: {saga.Data.FailureReason}"
            : $"Not sent: {saga.Data.FailureReason}. The reserved money is back in your wallet";
        Finish(saga, BankTransferStates.Failed, BankTransferStatus.Failed, detail);
    }

    // ---- The bank's answer (Raast's callback, or a status query) ----

    private void OnBankAnswer(SagaContext<BankTransferData> saga, RaastTransferCallback callback) =>
        ApplyBankAnswer(saga, callback.Status, callback.RaastReference, callback.Reason, "the bank's confirmation");

    private void ApplyBankAnswer(SagaContext<BankTransferData> saga, string status, string? reference, string? reason, string source)
    {
        var waiting = saga.State is BankTransferStates.Sending or BankTransferStates.Queued or BankTransferStates.Verifying
            or BankTransferStates.AwaitingBank or BankTransferStates.NeedsReview;

        if (status == RaastTransferStatus.Completed)
        {
            if (waiting)
            {
                StartSettling(saga, reference, $"{BankOf(saga.Data).Name} credited the beneficiary (Raast reference {reference})");
            }
            else if (saga.State == BankTransferStates.Returning)
            {
                // Raast said no earlier, and now the bank says it paid out: money may have left twice over.
                Review(saga, $"{BankOf(saga.Data).Name} reports the transfer completed ({reference}) after it was being cancelled");
            }

            return;
        }

        if (status == RaastTransferStatus.Rejected && waiting)
        {
            saga.Data.RaastReference ??= reference;
            StartReturning(saga, $"bank.{reason ?? "rejected"}", $"{BankOf(saga.Data).Name} refused it ({Describe(reason)})");
            return;
        }

        if (waiting && saga.State != BankTransferStates.NeedsReview)
        {
            saga.Note($"Still pending according to {source}");
        }
    }

    // ---- Timer steps (I/O allowed) ----

    protected override async Task OnWakeAsync(SagaContext<BankTransferData> saga, IServiceProvider services, CancellationToken cancellationToken)
    {
        switch (saga.WakeReason, saga.State)
        {
            case (Send, BankTransferStates.Sending or BankTransferStates.Queued):
                await SendAsync(saga, services.GetRequiredService<IRaastGateway>(), cancellationToken);
                break;
            case (Verify, BankTransferStates.Verifying):
            case (StatusCheck, BankTransferStates.AwaitingBank):
                await CheckAsync(saga, services.GetRequiredService<IRaastGateway>(), cancellationToken);
                break;
            case (LedgerCheck, BankTransferStates.Reserving or BankTransferStates.Settling or BankTransferStates.Returning):
                ResendLedgerCommand(saga);
                break;
            default:
                // A timer that no longer fits the state (it was overtaken by a message): nothing to do.
                break;
        }
    }

    private async Task SendAsync(SagaContext<BankTransferData> saga, IRaastGateway raast, CancellationToken cancellationToken)
    {
        var data = saga.Data;
        var bank = BankOf(data);
        data.SendAttempts++;

        var result = await raast.SendAsync(
            new RaastSendRequest(saga.Id, data.AmountPaisa, data.ToIban, bank, _options.SenderName, data.Note),
            cancellationToken);

        switch (result.Outcome)
        {
            case RaastCallOutcome.Ok:
                data.RaastReference = result.RaastReference;
                data.AcceptedAt = saga.Now;
                StartWaiting(saga, BankTransferStates.AwaitingBank, $"Sent over Raast (reference {result.RaastReference}); waiting for {bank.Name}");
                break;

            case RaastCallOutcome.Unavailable:
                Queue(saga, bank, result.Detail);
                break;

            case RaastCallOutcome.Unknown:
                StartWaiting(saga, BankTransferStates.Verifying, $"No clear answer from Raast ({result.Detail}); checking whether it arrived");
                break;

            default:
                StartReturning(saga, result.Code ?? "raast.rejected", $"Raast refused it ({result.Detail})");
                break;
        }
    }

    private void Queue(SagaContext<BankTransferData> saga, RaastBank bank, string? detail)
    {
        var data = saga.Data;
        data.QueuedSince ??= saga.Now;
        if (saga.Now - data.QueuedSince.Value >= TimeSpan.FromHours(_options.MaxQueueHours))
        {
            StartReturning(saga, "payments.bank_unavailable", $"{bank.Name} was unavailable for more than {_options.MaxQueueHours} hours");
            return;
        }

        if (saga.State != BankTransferStates.Queued)
        {
            saga.TransitionTo(BankTransferStates.Queued, $"{bank.Name} is unavailable ({detail}); queued, it will be sent as soon as the bank is back");
            Publish(saga, $"{bank.Name} is unavailable; your transfer is queued", BankTransferStatus.Queued);
        }

        var retries = data.SendAttempts;
        saga.WakeIn(Backoff(_options.QueueRetryInitialSeconds, _options.QueueRetryMaxSeconds, retries, jitter: true), Send);
    }

    private async Task CheckAsync(SagaContext<BankTransferData> saga, IRaastGateway raast, CancellationToken cancellationToken)
    {
        var data = saga.Data;
        var bank = BankOf(data);
        data.StatusChecks++;

        var result = await raast.GetStatusAsync(saga.Id, bank, cancellationToken);
        if (result.Outcome == RaastCallOutcome.Ok && result.Status is RaastTransferStatus.Completed or RaastTransferStatus.Rejected)
        {
            ApplyBankAnswer(saga, result.Status, result.RaastReference, result.Reason, "Raast");
            return;
        }

        if (result.Outcome == RaastCallOutcome.NotFound && saga.State == BankTransferStates.Verifying)
        {
            // Raast never received it, so sending it (again) cannot pay twice.
            saga.TransitionTo(BankTransferStates.Sending, "Raast never received it; sending again");
            saga.WakeNow(Send);
            return;
        }

        if (result.Outcome == RaastCallOutcome.Ok && saga.State == BankTransferStates.Verifying)
        {
            // Pending: it arrived after all.
            data.RaastReference = result.RaastReference;
            data.AcceptedAt ??= saga.Now;
            StartWaiting(saga, BankTransferStates.AwaitingBank, $"Raast has it (reference {result.RaastReference}); waiting for {bank.Name}");
            return;
        }

        // Still pending, or no answer to the question either: ask again later, but not for ever.
        if (saga.Now - (data.WaitingSince ?? saga.Now) >= TimeSpan.FromHours(_options.MaxWaitHours))
        {
            Review(saga, $"No final answer from {bank.Name} after {_options.MaxWaitHours} hours");
            return;
        }

        var reason = saga.State == BankTransferStates.Verifying ? Verify : StatusCheck;
        var first = saga.State == BankTransferStates.Verifying ? _options.VerifyDelaySeconds : _options.StatusCheckInitialSeconds;
        saga.WakeIn(Backoff(first, _options.StatusCheckMaxSeconds, data.StatusChecks, jitter: false), reason);
    }

    private void ResendLedgerCommand(SagaContext<BankTransferData> saga)
    {
        var data = saga.Data;
        data.LedgerResends++;
        if (data.LedgerResends > _options.MaxLedgerResends)
        {
            switch (saga.State)
            {
                case BankTransferStates.Reserving:
                    // The Ledger never answered: make sure nothing stays reserved, then give up.
                    StartReturning(saga, "payments.ledger_unavailable", "The wallet service did not answer");
                    return;
                default:
                    Review(saga, $"The wallet service did not answer while {(saga.State == BankTransferStates.Settling ? "settling" : "returning")} the money");
                    return;
            }
        }

        saga.Send(saga.State switch
        {
            BankTransferStates.Reserving => PlaceHoldFor(saga),
            BankTransferStates.Settling => CaptureHoldFor(saga),
            _ => new ReleaseHold(saga.Id, data.FailureReason ?? "Bank transfer cancelled"),
        });
        saga.WakeIn(LedgerReplyTimeout, LedgerCheck);
    }

    // ---- Moves ----

    private void StartWaiting(SagaContext<BankTransferData> saga, string state, string detail)
    {
        var data = saga.Data;
        var wasQueued = data.Status == BankTransferStatus.Queued;
        data.WaitingSince = saga.Now;
        data.StatusChecks = 0;
        saga.TransitionTo(state, detail);
        if (wasQueued)
        {
            Publish(saga, $"{BankOf(data).Name} is back; your transfer is on its way", BankTransferStatus.Processing);
        }

        saga.WakeIn(TimeSpan.FromSeconds(state == BankTransferStates.Verifying ? _options.VerifyDelaySeconds : _options.StatusCheckInitialSeconds), state == BankTransferStates.Verifying ? Verify : StatusCheck);
    }

    private void StartSettling(SagaContext<BankTransferData> saga, string? reference, string detail)
    {
        saga.Data.RaastReference ??= reference;
        saga.Data.LedgerResends = 0;
        saga.TransitionTo(BankTransferStates.Settling, detail);
        saga.Send(CaptureHoldFor(saga));
        saga.WakeIn(LedgerReplyTimeout, LedgerCheck);
    }

    private void StartReturning(SagaContext<BankTransferData> saga, string code, string reason)
    {
        saga.Data.FailureCode = code;
        saga.Data.FailureReason = reason;
        saga.Data.LedgerResends = 0;
        saga.TransitionTo(BankTransferStates.Returning, $"{reason}; returning the reserved money to your wallet");
        saga.Send(new ReleaseHold(saga.Id, reason));
        saga.WakeIn(LedgerReplyTimeout, LedgerCheck);
    }

    private static void Review(SagaContext<BankTransferData> saga, string detail)
    {
        saga.TransitionTo(BankTransferStates.NeedsReview, detail);
        saga.CancelWake();
    }

    private static void Finish(SagaContext<BankTransferData> saga, string state, string status, string detail)
    {
        saga.Data.FinishedAt = saga.Now;
        saga.Data.Status = status;
        saga.Complete(state, detail);
        Publish(saga, detail, status);
    }

    // ---- Messages ----

    private PlaceHold PlaceHoldFor(SagaContext<BankTransferData> saga)
    {
        var data = saga.Data;
        return new PlaceHold(
            saga.Id,
            data.UserId,
            data.AmountPaisa,
            JournalPurpose,
            $"Bank transfer to {Beneficiary(data)}",
            saga.Id.ToString(),
            (int)TimeSpan.FromHours(_options.HoldLifetimeHours).TotalSeconds,
            data.DeviceId);
    }

    private static CaptureHold CaptureHoldFor(SagaContext<BankTransferData> saga)
    {
        var data = saga.Data;
        return new CaptureHold(
            saga.Id,
            saga.Id,
            JournalPurpose,
            $"Raast transfer to {Beneficiary(data)}{(data.RaastReference is null ? "" : $" ({data.RaastReference})")}",
            [new HoldCredit(BankFloatAccount, data.AmountPaisa)]);
    }

    /// <summary>Tells the customer's apps (and later notifications) what happened.</summary>
    private static void Publish(SagaContext<BankTransferData> saga, string detail, string? status = null)
    {
        var data = saga.Data;
        data.Status = status ?? data.Status;
        saga.Send(new BankTransferChanged(saga.Id, data.UserId, data.Status, saga.State, data.AmountPaisa, data.ToBank, detail, saga.Now, data.RaastReference));
    }

    // ---- Helpers ----

    private TimeSpan LedgerReplyTimeout => TimeSpan.FromSeconds(_options.LedgerReplyTimeoutSeconds);

    /// <summary>first, 2×first, 4×first ... up to max; with jitter, up to a fifth less (so queued transfers do not all retry at once).</summary>
    internal static TimeSpan Backoff(int firstSeconds, int maxSeconds, int attempt, bool jitter)
    {
        var seconds = Math.Min(maxSeconds, firstSeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 20)));
        if (jitter)
        {
            seconds *= 1 - (Random.Shared.NextDouble() * 0.2);
        }

        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    private static RaastBank BankOf(BankTransferData data) =>
        RaastBanks.ByCode(data.ToBank) ?? throw new InvalidOperationException($"Unknown bank '{data.ToBank}'.");

    private static string Beneficiary(BankTransferData data) =>
        $"{data.BeneficiaryTitle ?? Iban.Mask(data.ToIban)} at {BankOf(data).Name}";

    internal static string Rupees(long paisa) =>
        string.Create(CultureInfo.InvariantCulture, $"Rs. {paisa / 100m:#,0.##}");

    private static string Describe(string? reason) => reason switch
    {
        "account_closed" => "the account is closed",
        "account_blocked" => "the account is blocked",
        null or "" => "no reason given",
        _ => reason.Replace('_', ' '),
    };
}
