using WalletPK.Identity.Domain.Kyc;

namespace WalletPK.Identity.Application.Abstractions;

public enum NadraOutcome
{
    /// <summary>The CNIC exists and the name and date of birth match.</summary>
    Verified = 0,

    /// <summary>No citizen with this CNIC.</summary>
    NotFound = 1,

    /// <summary>The CNIC exists but the name or date of birth do not match it.</summary>
    Mismatch = 2,

    /// <summary>NADRA could not be reached; the customer should try again later.</summary>
    Unavailable = 3,
}

/// <param name="Outcome">The verification outcome.</param>
/// <param name="Reference">NADRA's transaction reference when verified.</param>
public sealed record NadraVerification(NadraOutcome Outcome, string? Reference);

/// <summary>
/// Verifies a citizen's identity with NADRA (the national database). Simulated in-process for now;
/// replaced by an HTTP client to the mock provider in M5.5, wrapped in retries and a circuit breaker in M6.
/// </summary>
public interface INadraClient
{
    Task<NadraVerification> VerifyAsync(Cnic cnic, string fullName, DateOnly dateOfBirth, CancellationToken cancellationToken);
}
