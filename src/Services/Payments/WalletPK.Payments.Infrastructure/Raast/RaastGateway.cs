using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WalletPK.Payments.Application.Raast;
using WalletPK.Payments.Domain;
using WalletPK.Resilience;

namespace WalletPK.Payments.Infrastructure.Raast;

/// <summary>
/// Raast calls (meter "WalletPK.Payments"): <c>walletpk.payments.raast.calls</c> by operation, bank and
/// outcome, and <c>walletpk.payments.raast.duration</c>.
/// </summary>
public sealed class RaastMetrics
{
    public const string MeterName = "WalletPK.Payments";

    private readonly Counter<long> _calls;
    private readonly Histogram<double> _duration;

    public RaastMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _calls = meter.CreateCounter<long>("walletpk.payments.raast.calls", description: "Calls to Raast, by outcome");
        _duration = meter.CreateHistogram<double>("walletpk.payments.raast.duration", unit: "s", description: "Raast calls, retries included");
    }

    public void Record(string operation, string bank, RaastCallOutcome outcome, TimeSpan elapsed)
    {
        var tags = new TagList { { "operation", operation }, { "bank", bank }, { "outcome", outcome.ToString() } };
        _calls.Add(1, tags);
        _duration.Record(elapsed.TotalSeconds, tags);
    }
}

/// <summary>
/// Calls Raast over HTTP through the bank's resilience pipeline and turns every ending into a
/// <see cref="RaastCallOutcome"/>: answered, refused, unknown to Raast, certainly not done (safe to
/// queue and resend), or unknown (ask before resending).
/// </summary>
internal sealed partial class RaastGateway(
    HttpClient http,
    RaastResilience resilience,
    RaastMetrics metrics,
    ILogger<RaastGateway> logger) : IRaastGateway
{
    public async Task<RaastLookupResult> LookupAliasAsync(string mobileE164, CancellationToken cancellationToken)
    {
        var call = await CallAsync("alias", RaastResilience.RaastKey, token => http.GetAsync($"raast/v1/aliases/{Uri.EscapeDataString(mobileE164)}", token), cancellationToken);
        if (call.Outcome != RaastCallOutcome.Ok)
        {
            return new(call.Outcome, null, call.Code, call.Detail);
        }

        var alias = call.Body.Deserialize<AliasAnswer>(JsonSerializerOptions.Web)!;
        var bank = RaastBanks.ByCode(alias.Bank);

        // The alias answer has no account status: the title fetch has it (best effort).
        var status = "Unknown";
        if (bank is not null && await FetchTitleAsync(alias.Iban, bank, cancellationToken) is { Outcome: RaastCallOutcome.Ok, Account: { } account })
        {
            status = account.Status;
        }

        return new(RaastCallOutcome.Ok, new RaastAccount(alias.Iban, alias.Bank, alias.BankName, alias.Title, status), null, null);
    }

    public async Task<RaastLookupResult> FetchTitleAsync(string iban, RaastBank bank, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bank);
        var call = await CallAsync("title", bank.Code, token => http.GetAsync($"raast/v1/accounts/{Uri.EscapeDataString(iban)}", token), cancellationToken);
        if (call.Outcome != RaastCallOutcome.Ok)
        {
            return new(call.Outcome, null, call.Code, call.Detail);
        }

        var account = call.Body.Deserialize<AccountAnswer>(JsonSerializerOptions.Web)!;
        return new(RaastCallOutcome.Ok, new RaastAccount(account.Iban, account.Bank, account.BankName, account.Title, account.Status), null, null);
    }

    public async Task<RaastSendResult> SendAsync(RaastSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new SendBody(request.TransferId.ToString(), request.AmountPaisa, request.ToIban, request.SenderName, request.Purpose);
        var call = await CallAsync("send", request.Bank.Code, token => http.PostAsJsonAsync("raast/v1/transfers", body, JsonSerializerOptions.Web, token), cancellationToken);
        return call.Outcome == RaastCallOutcome.Ok
            ? new(RaastCallOutcome.Ok, call.Body.Deserialize<TransferAnswer>(JsonSerializerOptions.Web)!.RaastReference, null, null)
            : new(call.Outcome, null, call.Code, call.Detail);
    }

    public async Task<RaastStatusResult> GetStatusAsync(Guid transferId, RaastBank bank, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bank);

        // The status query is answered by Raast itself, even while the beneficiary bank is down.
        var call = await CallAsync("status", RaastResilience.RaastKey, token => http.GetAsync($"raast/v1/transfers/{transferId}", token), cancellationToken, bank.Code);
        if (call.Outcome != RaastCallOutcome.Ok)
        {
            return new(call.Outcome, null, null, null, call.Detail);
        }

        var transfer = call.Body.Deserialize<TransferAnswer>(JsonSerializerOptions.Web)!;
        return new(RaastCallOutcome.Ok, transfer.Status, transfer.RaastReference, transfer.Reason, null);
    }

    private async Task<CallResult> CallAsync(
        string operation,
        string pipelineKey,
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken,
        string? bankTag = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await ExecuteAsync(pipelineKey, send, cancellationToken);
        metrics.Record(operation, bankTag ?? pipelineKey, result.Outcome, stopwatch.Elapsed);
        if (result.Outcome is RaastCallOutcome.Unavailable or RaastCallOutcome.Unknown)
        {
            LogCallFailed(logger, operation, bankTag ?? pipelineKey, result.Outcome, result.Detail);
        }

        return result;
    }

    private async Task<CallResult> ExecuteAsync(string pipelineKey, Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        var result = await resilience.Pipelines.CallAsync(pipelineKey, send, cancellationToken);
        var outcome = result.Outcome switch
        {
            ProviderOutcome.Ok => RaastCallOutcome.Ok,
            ProviderOutcome.Rejected => RaastCallOutcome.Rejected,
            ProviderOutcome.NotFound => RaastCallOutcome.NotFound,
            ProviderOutcome.Unavailable => RaastCallOutcome.Unavailable,
            _ => RaastCallOutcome.Unknown,
        };
        var detail = result.Detail switch
        {
            "circuit open: the provider is failing" => "circuit open: the bank is failing",
            "too many calls in flight" => "too many calls to the bank in flight",
            _ => result.Detail,
        };
        return new(outcome, result.Body, result.Code, detail);
    }

    private sealed record CallResult(RaastCallOutcome Outcome, JsonElement Body, string? Code, string? Detail);

    private sealed record SendBody(string TransferId, long AmountPaisa, string ToIban, string? SenderName, string? Purpose);

    private sealed record AliasAnswer(string Alias, string Iban, string Bank, string BankName, string Title);

    private sealed record AccountAnswer(string Iban, string Bank, string BankName, string Title, string Status);

    private sealed record TransferAnswer(string TransferId, string RaastReference, string Status, string? Reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Raast {Operation} for {Bank}: {Outcome} ({Detail})")]
    private static partial void LogCallFailed(ILogger logger, string operation, string bank, RaastCallOutcome outcome, string? detail);
}
