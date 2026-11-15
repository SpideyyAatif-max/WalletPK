using System.Diagnostics.Metrics;
using WalletPK.Common.Results;
using WalletPK.Web.Errors;

namespace WalletPK.MockProviders.Faults;

/// <summary>What the dice decided for one call.</summary>
public enum FaultOutcome
{
    None = 0,
    Outage = 1,
    Error = 2,
    Timeout = 3,
    AcceptButTimeout = 4,
}

/// <summary>Picks the random numbers. Replaceable so tests can force an outcome.</summary>
public interface IFaultDice
{
    /// <summary>A number in [0, 1).</summary>
    double Roll();
}

internal sealed class RandomFaultDice : IFaultDice
{
    // Not security relevant: picks which simulated calls fail.
#pragma warning disable CA5394
    public double Roll() => Random.Shared.NextDouble();
#pragma warning restore CA5394
}

/// <summary>
/// Wraps a provider operation in that provider's faults. A call can involve several providers (a Raast
/// transfer to HBL is "raast" and "bank:HBL"): their latencies add up and the first fault that fires wins.
/// </summary>
public sealed partial class FaultInjector(FaultRegistry registry, IFaultDice dice, IMeterFactory meterFactory, ILogger<FaultInjector> logger)
{
    private readonly Counter<long> _injected = meterFactory.Create("WalletPK.MockProviders").CreateCounter<long>(
        "walletpk.mock.faults_injected", description: "Faults the mock providers injected, by provider and kind");

    /// <summary>Runs <paramref name="operation"/> unless a fault says otherwise, and returns what the caller gets.</summary>
    public async Task<IResult> RunAsync(HttpContext context, IReadOnlyList<string> providers, Func<Task<IResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(operation);

        var (outcome, provider, settings, delay) = Decide(providers);
        var aborted = context.RequestAborted;

        if (outcome == FaultOutcome.Outage)
        {
            Record(provider!, outcome);
            if (settings!.OutageMode == OutageMode.ConnectionReset)
            {
                context.Abort();
                return Results.Empty;
            }

            return Error.Unavailable("provider.unavailable", $"{provider} is unavailable (simulated outage).").ToProblem();
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, aborted);
        }

        switch (outcome)
        {
            case FaultOutcome.Error:
                Record(provider!, outcome);
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Provider error",
                    detail: $"{provider} failed to process the request (simulated error). Nothing was done.",
                    extensions: new Dictionary<string, object?> { ["code"] = "provider.error" });

            case FaultOutcome.Timeout:
                Record(provider!, outcome);
                await Task.Delay(settings!.TimeoutMs, aborted);
                return Results.Problem(
                    statusCode: StatusCodes.Status504GatewayTimeout,
                    title: "Provider timeout",
                    detail: $"{provider} did not answer in time (simulated timeout). Nothing was done.",
                    extensions: new Dictionary<string, object?> { ["code"] = "provider.timeout" });

            case FaultOutcome.AcceptButTimeout:
                Record(provider!, outcome);
                await operation(); // The work is done and stored ...
                LogAcceptedButSilent(logger, provider!, context.Request.Path);
                await Task.Delay(settings!.TimeoutMs, aborted); // ... but the answer never comes.
                context.Abort();
                return Results.Empty;

            default:
                return await operation();
        }
    }

    private (FaultOutcome Outcome, string? Provider, FaultSettings? Settings, TimeSpan Delay) Decide(IReadOnlyList<string> providers)
    {
        var delay = TimeSpan.Zero;
        (FaultOutcome, string?, FaultSettings?) fired = (FaultOutcome.None, null, null);

        foreach (var provider in providers)
        {
            if (registry.For(provider) is not { } settings)
            {
                continue;
            }

            if (settings.Outage)
            {
                return (FaultOutcome.Outage, provider, settings, TimeSpan.Zero);
            }

            delay += TimeSpan.FromMilliseconds(settings.LatencyMs + (settings.JitterMs > 0 ? dice.Roll() * settings.JitterMs : 0));

            if (fired.Item1 != FaultOutcome.None)
            {
                continue;
            }

            var roll = dice.Roll();
            var outcome = roll < settings.ErrorRate ? FaultOutcome.Error
                : roll < settings.ErrorRate + settings.TimeoutRate ? FaultOutcome.Timeout
                : roll < settings.ErrorRate + settings.TimeoutRate + settings.AcceptButTimeoutRate ? FaultOutcome.AcceptButTimeout
                : FaultOutcome.None;
            if (outcome != FaultOutcome.None)
            {
                fired = (outcome, provider, settings);
            }
        }

        return (fired.Item1, fired.Item2, fired.Item3, delay);
    }

    private void Record(string provider, FaultOutcome outcome) =>
        _injected.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("kind", outcome.ToString()));

    [LoggerMessage(Level = LogLevel.Information, Message = "{Provider} accepted {Path} but will not answer (accept-but-timeout)")]
    private static partial void LogAcceptedButSilent(ILogger logger, string provider, PathString path);
}
