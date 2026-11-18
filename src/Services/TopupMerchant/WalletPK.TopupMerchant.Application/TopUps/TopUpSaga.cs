using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Contracts.Ledger;
using WalletPK.Contracts.TopupMerchant;
using WalletPK.Sagas;
using WalletPK.TopupMerchant.Domain;

namespace WalletPK.TopupMerchant.Application.TopUps;

/// <summary>A top-up's data, stored as JSON in its saga_instances row.</summary>
public sealed class TopUpData
{
    public Guid UserId { get; set; }

    public string? DeviceId { get; set; }

    public string IdempotencyKey { get; set; } = "";

    public string Network { get; set; } = "";

    public string Msisdn { get; set; } = "";

    public long AmountPaisa { get; set; }

    public string? BundleCode { get; set; }

    public string? BundleName { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public string Status { get; set; } = TopUpStatus.Processing;

    public string? TelcoReference { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureReason { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? RetryingSince { get; set; }

    public DateTimeOffset? VerifyingSince { get; set; }

    public int StatusChecks { get; set; }

    public int LedgerResends { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Top-up settings, from "TopUps".</summary>
public sealed class TopUpOptions
{
    public const string SectionName = "TopUps";

    public int HoldLifetimeHours { get; set; } = 72;

    public int LedgerReplyTimeoutSeconds { get; set; } = 60;

    public int MaxLedgerResends { get; set; } = 60;

    public int MaxAttempts { get; set; } = 5;

    public int RetryInitialSeconds { get; set; } = 5;

    public int RetryMaxSeconds { get; set; } = 60;

    /// <summary>A network down this long: the top-up is given up and the money returned.</summary>
    public int MaxRetryMinutes { get; set; } = 10;

    public int VerifyDelaySeconds { get; set; } = 5;

    public int VerifyMaxSeconds { get; set; } = 300;

    /// <summary>Unconfirmed this long: dead-lettered (topup.dlq).</summary>
    public int MaxVerifyHours { get; set; } = 24;

    public bool IsValid() =>
        HoldLifetimeHours is >= 1 and <= 168 && LedgerReplyTimeoutSeconds >= 1 && MaxLedgerResends >= 1 && MaxAttempts >= 1
        && RetryInitialSeconds >= 1 && RetryMaxSeconds >= RetryInitialSeconds && MaxRetryMinutes >= 1
        && VerifyDelaySeconds >= 1 && VerifyMaxSeconds >= VerifyDelaySeconds && MaxVerifyHours >= 1
        && HoldLifetimeHours * 60 > MaxRetryMinutes + (MaxVerifyHours * 60);
}

/// <summary>
/// A mobile top-up (M8.1): reserve the money, top up the number, settle to TELCO_SETTLEMENT, or return the
/// money. Like a bill: an unavailable network is retried (given up after 10 minutes), and a timeout is
/// checked with the network's status query before anything is sent again, so a number is never topped up
/// twice. Unconfirmed for 24 hours: dead-lettered (topup.dlq) with the money still reserved.
/// </summary>
public sealed class TopUpSaga : SagaDefinition<TopUpData>
{
    public const string SagaName = "topup";

    public const string JournalPurpose = "MobileTopup";

    public const string TelcoSettlementAccount = "TELCO_SETTLEMENT";

    private const string Send = "send";
    private const string Verify = "verify";
    private const string LedgerCheck = "ledger-check";

    private readonly TopUpOptions _options;

    public TopUpSaga(TopUpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        On<HoldPlaced>(OnHoldPlaced);
        On<HoldRejected>(OnHoldRejected);
        On<HoldCaptured>(OnHoldCaptured);
        On<HoldCaptureFailed>((saga, reply) =>
        {
            if (saga.State == TopUpStates.Settling)
            {
                DeadLetter(saga, $"The number was topped up but settling the wallet failed ({reply.Code})");
            }
        });
        On<HoldReleased>(OnHoldReleased);
    }

    public override string Name => SagaName;

    protected override void OnStart(SagaContext<TopUpData> saga)
    {
        saga.TransitionTo(TopUpStates.Reserving, $"{What(saga.Data)} for {saga.Data.Msisdn} requested");
        saga.Send(PlaceHoldFor(saga));
        saga.WakeIn(LedgerTimeout, LedgerCheck);
    }

    private static void OnHoldPlaced(SagaContext<TopUpData> saga, HoldPlaced reply)
    {
        if (saga.State != TopUpStates.Reserving)
        {
            return;
        }

        saga.Data.LedgerResends = 0;
        saga.TransitionTo(TopUpStates.ToppingUp, $"{Rupees(reply.AmountPaisa)} reserved in your wallet");
        saga.WakeNow(Send);
    }

    private static void OnHoldRejected(SagaContext<TopUpData> saga, HoldRejected reply)
    {
        if (saga.State == TopUpStates.Reserving)
        {
            saga.Data.FailureCode = reply.Code;
            saga.Data.FailureReason = reply.Reason;
            Finish(saga, TopUpStates.Failed, TopUpStatus.Failed, $"Not topped up: {reply.Reason}");
        }
    }

    private static void OnHoldCaptured(SagaContext<TopUpData> saga, HoldCaptured reply)
    {
        if (saga.State == TopUpStates.Settling)
        {
            Finish(saga, TopUpStates.Completed, TopUpStatus.Completed, $"{What(saga.Data)} added to {saga.Data.Msisdn} (reference {saga.Data.TelcoReference})");
        }
    }

    private static void OnHoldReleased(SagaContext<TopUpData> saga, HoldReleased reply)
    {
        if (saga.State == TopUpStates.Returning)
        {
            Finish(saga, TopUpStates.Failed, TopUpStatus.Failed, $"Not topped up: {saga.Data.FailureReason}. The reserved money is back in your wallet");
        }
    }

    protected override async Task OnWakeAsync(SagaContext<TopUpData> saga, IServiceProvider services, CancellationToken cancellationToken)
    {
        switch (saga.WakeReason, saga.State)
        {
            case (Send, TopUpStates.ToppingUp or TopUpStates.Retrying):
                await SendAsync(saga, services.GetRequiredService<ITelcoGateway>(), cancellationToken);
                break;
            case (Verify, TopUpStates.Verifying):
                await VerifyAsync(saga, services.GetRequiredService<ITelcoGateway>(), cancellationToken);
                break;
            case (LedgerCheck, TopUpStates.Reserving or TopUpStates.Settling or TopUpStates.Returning):
                ResendLedgerCommand(saga);
                break;
            default:
                break;
        }
    }

    private async Task SendAsync(SagaContext<TopUpData> saga, ITelcoGateway telcos, CancellationToken cancellationToken)
    {
        var data = saga.Data;
        var network = NetworkOf(data);
        data.Attempts++;
        var result = await telcos.TopUpAsync(network, saga.Id, data.Msisdn, data.AmountPaisa, data.BundleCode, cancellationToken);

        switch (result.Outcome)
        {
            case TelcoCallOutcome.Ok:
                StartSettling(saga, result.Receipt!.TelcoReference, $"{network.Name} topped up {data.Msisdn} (reference {result.Receipt.TelcoReference})");
                break;
            case TelcoCallOutcome.Unavailable:
                data.RetryingSince ??= saga.Now;
                if (data.Attempts >= _options.MaxAttempts || saga.Now - data.RetryingSince.Value >= TimeSpan.FromMinutes(_options.MaxRetryMinutes))
                {
                    StartReturning(saga, "topup.network_unavailable", $"{network.Name} was unavailable ({result.Detail})");
                    break;
                }

                if (saga.State != TopUpStates.Retrying)
                {
                    saga.TransitionTo(TopUpStates.Retrying, $"{network.Name} is unavailable ({result.Detail}); trying again shortly");
                }

                saga.WakeIn(Backoff(_options.RetryInitialSeconds, _options.RetryMaxSeconds, data.Attempts), Send);
                break;
            case TelcoCallOutcome.Unknown:
                data.VerifyingSince = saga.Now;
                data.StatusChecks = 0;
                saga.TransitionTo(TopUpStates.Verifying, $"No answer from {network.Name} ({result.Detail}); checking whether the top-up arrived");
                saga.WakeIn(TimeSpan.FromSeconds(_options.VerifyDelaySeconds), Verify);
                break;
            default:
                StartReturning(saga, result.Code ?? "topup.refused", $"{network.Name} refused it ({result.Detail})");
                break;
        }
    }

    private async Task VerifyAsync(SagaContext<TopUpData> saga, ITelcoGateway telcos, CancellationToken cancellationToken)
    {
        var data = saga.Data;
        var network = NetworkOf(data);
        data.StatusChecks++;
        var result = await telcos.GetTopUpAsync(network, saga.Id, cancellationToken);

        if (result.Outcome == TelcoCallOutcome.Ok)
        {
            StartSettling(saga, result.Receipt!.TelcoReference, $"{network.Name} confirms the top-up arrived (reference {result.Receipt.TelcoReference})");
            return;
        }

        if (result.Outcome == TelcoCallOutcome.NotFound)
        {
            if (data.Attempts >= _options.MaxAttempts)
            {
                StartReturning(saga, "topup.network_unreachable", $"{network.Name} never received the top-up after {data.Attempts} attempts");
                return;
            }

            saga.TransitionTo(TopUpStates.ToppingUp, $"{network.Name} never received it; sending again");
            saga.WakeNow(Send);
            return;
        }

        if (saga.Now - (data.VerifyingSince ?? saga.Now) >= TimeSpan.FromHours(_options.MaxVerifyHours))
        {
            DeadLetter(saga, $"{network.Name} has not confirmed or denied the top-up for {_options.MaxVerifyHours} hours");
            return;
        }

        saga.WakeIn(Backoff(_options.VerifyDelaySeconds, _options.VerifyMaxSeconds, data.StatusChecks), Verify);
    }

    private void ResendLedgerCommand(SagaContext<TopUpData> saga)
    {
        var data = saga.Data;
        data.LedgerResends++;
        if (data.LedgerResends > _options.MaxLedgerResends)
        {
            if (saga.State == TopUpStates.Reserving)
            {
                StartReturning(saga, "topup.ledger_unavailable", "The wallet service did not answer");
            }
            else
            {
                DeadLetter(saga, "The wallet service did not answer");
            }

            return;
        }

        saga.Send(saga.State switch
        {
            TopUpStates.Reserving => PlaceHoldFor(saga),
            TopUpStates.Settling => CaptureHoldFor(saga),
            _ => new ReleaseHold(saga.Id, data.FailureReason ?? "Top-up cancelled"),
        });
        saga.WakeIn(LedgerTimeout, LedgerCheck);
    }

    private void StartSettling(SagaContext<TopUpData> saga, string reference, string detail)
    {
        saga.Data.TelcoReference = reference;
        saga.Data.LedgerResends = 0;
        saga.TransitionTo(TopUpStates.Settling, detail);
        saga.Send(CaptureHoldFor(saga));
        saga.WakeIn(LedgerTimeout, LedgerCheck);
    }

    private void StartReturning(SagaContext<TopUpData> saga, string code, string reason)
    {
        saga.Data.FailureCode = code;
        saga.Data.FailureReason = reason;
        saga.Data.LedgerResends = 0;
        saga.TransitionTo(TopUpStates.Returning, $"{reason}; returning the reserved money to your wallet");
        saga.Send(new ReleaseHold(saga.Id, reason));
        saga.WakeIn(LedgerTimeout, LedgerCheck);
    }

    private static void DeadLetter(SagaContext<TopUpData> saga, string reason)
    {
        var data = saga.Data;
        var last = saga.State;
        data.Status = TopUpStatus.UnderReview;
        saga.TransitionTo(TopUpStates.DeadLettered, $"{reason}; our team will resolve it (the money stays reserved)");
        saga.CancelWake();
        saga.Send(new TopUpDeadLettered(saga.Id, data.UserId, data.Network, data.Msisdn, data.AmountPaisa, last, reason, saga.Now));
        Publish(saga, reason);
    }

    private static void Finish(SagaContext<TopUpData> saga, string state, string status, string detail)
    {
        saga.Data.Status = status;
        saga.Data.FinishedAt = saga.Now;
        saga.Complete(state, detail);
        Publish(saga, detail);
    }

    private static void Publish(SagaContext<TopUpData> saga, string detail)
    {
        var data = saga.Data;
        saga.Send(new TopUpChanged(saga.Id, data.UserId, data.Network, data.Msisdn, data.BundleCode, data.AmountPaisa, data.Status, saga.State, detail, saga.Now));
    }

    private PlaceHold PlaceHoldFor(SagaContext<TopUpData> saga)
    {
        var data = saga.Data;
        return new PlaceHold(
            saga.Id, data.UserId, data.AmountPaisa, JournalPurpose, $"{What(data)} for {data.Msisdn}", saga.Id.ToString(),
            (int)TimeSpan.FromHours(_options.HoldLifetimeHours).TotalSeconds, data.DeviceId);
    }

    private static CaptureHold CaptureHoldFor(SagaContext<TopUpData> saga)
    {
        var data = saga.Data;
        return new CaptureHold(saga.Id, saga.Id, JournalPurpose, $"{What(data)} for {data.Msisdn} ({data.TelcoReference})", [new HoldCredit(TelcoSettlementAccount, data.AmountPaisa)]);
    }

    private TimeSpan LedgerTimeout => TimeSpan.FromSeconds(_options.LedgerReplyTimeoutSeconds);

    internal static TimeSpan Backoff(int firstSeconds, int maxSeconds, int attempt) =>
        TimeSpan.FromSeconds(Math.Min(maxSeconds, firstSeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 20))));

    private static Network NetworkOf(TopUpData data) =>
        Networks.ByCode(data.Network) ?? throw new InvalidOperationException($"Unknown network '{data.Network}'.");

    private static string What(TopUpData data) =>
        data.BundleName is { } bundle ? $"{NetworkOf(data).Name} {bundle} bundle ({Rupees(data.AmountPaisa)})" : $"{Rupees(data.AmountPaisa)} {NetworkOf(data).Name} airtime";

    internal static string Rupees(long paisa) => string.Create(CultureInfo.InvariantCulture, $"Rs. {paisa / 100m:#,0.##}");
}
