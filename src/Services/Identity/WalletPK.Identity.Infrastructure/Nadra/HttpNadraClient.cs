using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Kyc;

namespace WalletPK.Identity.Infrastructure.Nadra;

/// <summary>Where NADRA verification goes, from the "Nadra" section.</summary>
public sealed class NadraOptions
{
    public const string SectionName = "Nadra";

    /// <summary>"Simulated" (in-process stand-in, the default) or "Http" (the mock provider, M5.5).</summary>
    public string Mode { get; set; } = "Simulated";

    /// <summary>The NADRA service, e.g. http://mock-providers:8080/ (Http mode).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Sent as X-Api-Key (Http mode).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Give up after this long and report NADRA as unavailable.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    public bool UsesHttp => string.Equals(Mode, "Http", StringComparison.OrdinalIgnoreCase);

    public bool IsValid() =>
        (UsesHttp || string.Equals(Mode, "Simulated", StringComparison.OrdinalIgnoreCase))
        && (!UsesHttp || (Uri.TryCreate(BaseUrl, UriKind.Absolute, out _) && ApiKey.Length > 0))
        && TimeoutSeconds is >= 1 and <= 60;
}

/// <summary>
/// Verifies identities with NADRA over HTTP (the mock provider's POST /nadra/v1/verifications). Anything
/// other than a clear answer (an error, a timeout, an outage) is reported as Unavailable, so the customer
/// is told to try again rather than wrongly refused. Retries and a circuit breaker come with Polly in M6.
/// </summary>
internal sealed partial class HttpNadraClient(HttpClient http, ILogger<HttpNadraClient> logger) : INadraClient
{
    public async Task<NadraVerification> VerifyAsync(Cnic cnic, string fullName, DateOnly dateOfBirth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cnic);

        try
        {
            using var response = await http.PostAsJsonAsync(
                "nadra/v1/verifications",
                new { cnic = cnic.Value, fullName, dateOfBirth },
                cancellationToken);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                LogUnavailable(logger, cnic.Masked, $"HTTP {(int)response.StatusCode}");
                return new NadraVerification(NadraOutcome.Unavailable, null);
            }

            var answer = await response.Content.ReadFromJsonAsync<Answer>(cancellationToken);
            var outcome = answer?.Result switch
            {
                "Verified" => NadraOutcome.Verified,
                "NotFound" => NadraOutcome.NotFound,
                "Mismatch" => NadraOutcome.Mismatch,
                _ => NadraOutcome.Unavailable,
            };
            LogVerification(logger, cnic.Masked, outcome);
            return new NadraVerification(outcome, outcome == NadraOutcome.Verified ? answer?.Reference : null);
        }
        catch (HttpRequestException exception)
        {
            LogUnavailable(logger, cnic.Masked, exception.Message);
            return new NadraVerification(NadraOutcome.Unavailable, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnavailable(logger, cnic.Masked, "timed out");
            return new NadraVerification(NadraOutcome.Unavailable, null);
        }
    }

    private sealed record Answer(string? Result, string? Reference);

    [LoggerMessage(Level = LogLevel.Information, Message = "NADRA check for {CnicMasked}: {Outcome}")]
    private static partial void LogVerification(ILogger logger, string cnicMasked, NadraOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NADRA unavailable for {CnicMasked}: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string cnicMasked, string reason);
}
