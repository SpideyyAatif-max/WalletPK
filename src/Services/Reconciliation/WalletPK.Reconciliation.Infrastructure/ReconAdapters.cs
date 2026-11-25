using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Contracts.Bills;
using WalletPK.Contracts.Payments;
using WalletPK.Contracts.TopupMerchant;
using WalletPK.Messaging.Abstractions;
using WalletPK.Reconciliation.Application;
using WalletPK.Reconciliation.Domain;

namespace WalletPK.Reconciliation.Infrastructure;

/// <summary>The payment services' events about payments that leave WalletPK (M12.1).</summary>
internal sealed class PaymentEventHandlers(PaymentTracker tracker) :
    IIntegrationEventHandler<BankTransferChanged>,
    IIntegrationEventHandler<BillPaymentChanged>,
    IIntegrationEventHandler<TopUpChanged>
{
    public Task HandleAsync(BankTransferChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return tracker.TrackAsync(new PaymentUpdate(
            integrationEvent.TransferId, PaymentTracker.BankTransfer, Parties.Bank(integrationEvent.ToBank), integrationEvent.UserId,
            integrationEvent.RaastReference, integrationEvent.AmountPaisa, integrationEvent.Status, integrationEvent.ChangedAt), cancellationToken);
    }

    public Task HandleAsync(BillPaymentChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return tracker.TrackAsync(new PaymentUpdate(
            integrationEvent.PaymentId, PaymentTracker.BillPayment, Parties.Biller(integrationEvent.Biller), integrationEvent.UserId,
            integrationEvent.BillerReference, integrationEvent.AmountPaisa, integrationEvent.Status, integrationEvent.ChangedAt), cancellationToken);
    }

    public Task HandleAsync(TopUpChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return tracker.TrackAsync(new PaymentUpdate(
            integrationEvent.TopUpId, PaymentTracker.TopUp, Parties.Telco(integrationEvent.Network), integrationEvent.UserId,
            integrationEvent.TelcoReference, integrationEvent.AmountPaisa, integrationEvent.Status, integrationEvent.ChangedAt), cancellationToken);
    }
}

/// <summary>Where the settlement files are fetched from, from "Providers:Settlement".</summary>
public sealed class SettlementSourceOptions
{
    public const string SectionName = "Providers:Settlement";

    public Uri? BaseUrl { get; set; }

    public string ApiKey { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 30;

    public bool IsValid() => BaseUrl is { IsAbsoluteUri: true } && ApiKey.Length > 0 && TimeoutSeconds is >= 1 and <= 300;
}

/// <summary>Fetches a party's file for a day from the providers (GET /settlement/{kind}/{code}/{date}).</summary>
internal sealed partial class SettlementClient(HttpClient http, ILogger<SettlementClient> logger) : ISettlementSource
{
    public async Task<SettlementFetch> FetchAsync(string party, DateOnly date, CancellationToken cancellationToken)
    {
        if (Parties.FilePath(party) is not { } path)
        {
            return new SettlementFetch(null, $"No settlement file location for '{party}'.");
        }

        var uri = new Uri($"settlement/{path}/{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", UriKind.Relative);
        try
        {
            using var response = await http.GetAsync(uri, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return new SettlementFetch(await response.Content.ReadAsStringAsync(cancellationToken), null);
            }

            LogRefused(logger, party, date, (int)response.StatusCode);
            return new SettlementFetch(null, $"The provider answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, party, date, exception);
            return new SettlementFetch(null, $"The provider could not be reached ({exception.GetType().Name}).");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The settlement file of {Party} for {Date} was refused: HTTP {Status}")]
    private static partial void LogRefused(ILogger logger, string party, DateOnly date, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The settlement file of {Party} for {Date} could not be fetched")]
    private static partial void LogFailed(ILogger logger, string party, DateOnly date, Exception exception);
}

/// <summary>
/// Reconciliation metrics: runs by party and outcome, breaks found by kind, and open breaks by kind
/// (walletpk.recon.breaks.open, refreshed after each run and by the scheduler).
/// </summary>
internal sealed class ReconMetrics : IReconMetrics
{
    private readonly Counter<long> _runs;
    private readonly Counter<long> _found;
    private volatile IReadOnlyDictionary<string, int> _open = new Dictionary<string, int>();

    public ReconMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create("WalletPK.Reconciliation");
        _runs = meter.CreateCounter<long>("walletpk.recon.runs", description: "Reconciliation runs, by party and outcome.");
        _found = meter.CreateCounter<long>("walletpk.recon.breaks.found", description: "New breaks, by kind.");
        meter.CreateObservableGauge("walletpk.recon.breaks.open", () => _open.Select(pair => new Measurement<int>(pair.Value, new KeyValuePair<string, object?>("kind", pair.Key))),
            description: "Open and disputed breaks, by kind.");
    }

    public void RunFinished(string party, string status) =>
        _runs.Add(1, new KeyValuePair<string, object?>("party", party), new KeyValuePair<string, object?>("status", status));

    public void BreaksFound(string kind, int count) => _found.Add(count, new KeyValuePair<string, object?>("kind", kind));

    public void OpenBreaks(IReadOnlyDictionary<string, int> byKind) => _open = byKind;
}

/// <summary>
/// The nightly run (M12.1): once the configured time has passed in Pakistan, reconciles yesterday for every
/// party not yet done, and tries a party whose file was not available again later.
/// </summary>
internal sealed partial class ReconciliationScheduler(
    IServiceScopeFactory scopes,
    ReconciliationOptions options,
    IReconMetrics metrics,
    TimeProvider timeProvider,
    ILogger<ReconciliationScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dailyAt = TimeOnly.Parse(options.Schedule.DailyAt, CultureInfo.InvariantCulture);
        var retryAfter = TimeSpan.FromMinutes(options.Schedule.RetryMinutes);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Schedule.PollSeconds), timeProvider);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IReconStore>();
                metrics.OpenBreaks(await store.OpenBreakCountsAsync(stoppingToken));
                if (!options.Schedule.Enabled)
                {
                    continue;
                }

                var now = timeProvider.GetUtcNow();
                var yesterday = ReconSchedule.Due(now, dailyAt, options.PartiesOrDefault, [], retryAfter).Date;
                var runs = await store.LatestRunsAsync(yesterday, yesterday, stoppingToken);
                var (date, parties) = ReconSchedule.Due(now, dailyAt, options.PartiesOrDefault, runs, retryAfter);
                if (parties.Count > 0)
                {
                    LogStarting(logger, date, parties.Count);
                    await scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync(date, parties, ReconciliationService.Scheduler, stoppingToken);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciling {Date} for {Count} parties")]
    private static partial void LogStarting(ILogger logger, DateOnly date, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The scheduled reconciliation failed; trying again at the next tick")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
