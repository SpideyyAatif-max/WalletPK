using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Kyc;

namespace WalletPK.Identity.Infrastructure.Nadra;

/// <summary>
/// Stand-in for NADRA until the mock provider exists (M5.5). Every well-formed CNIC verifies, except
/// these test prefixes, which let you try each failure on purpose (also from the UI):
/// <list type="table">
/// <item><term>70000-…</term><description>not found in NADRA</description></item>
/// <item><term>71111-…</term><description>name / date of birth do not match</description></item>
/// <item><term>72222-…</term><description>NADRA unavailable (outage)</description></item>
/// </list>
/// </summary>
internal sealed partial class SimulatedNadraClient(ILogger<SimulatedNadraClient> logger) : INadraClient
{
    public const string NotFoundPrefix = "70000";
    public const string MismatchPrefix = "71111";
    public const string UnavailablePrefix = "72222";

    public Task<NadraVerification> VerifyAsync(Cnic cnic, string fullName, DateOnly dateOfBirth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cnic);

        var outcome = cnic.Value[..5] switch
        {
            NotFoundPrefix => NadraOutcome.NotFound,
            MismatchPrefix => NadraOutcome.Mismatch,
            UnavailablePrefix => NadraOutcome.Unavailable,
            _ => NadraOutcome.Verified,
        };

        var reference = outcome == NadraOutcome.Verified
            ? "NADRA-SIM-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6))
            : null;

        // Only the masked CNIC is ever logged.
        LogVerification(logger, cnic.Masked, outcome);
        return Task.FromResult(new NadraVerification(outcome, reference));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Simulated NADRA check for {CnicMasked}: {Outcome}")]
    private static partial void LogVerification(ILogger logger, string cnicMasked, NadraOutcome outcome);
}
