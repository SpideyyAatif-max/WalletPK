using System.Security.Cryptography;
using Dapper;
using WalletPK.Common.Results;
using WalletPK.MockProviders.Faults;
using WalletPK.MockProviders.Security;
using WalletPK.MockProviders.Storage;
using WalletPK.Web.Errors;

namespace WalletPK.MockProviders.Nadra;

/// <param name="Cnic">13 digits, with or without dashes.</param>
/// <param name="FullName">As the customer typed it.</param>
/// <param name="DateOfBirth">yyyy-MM-dd.</param>
public sealed record NadraVerificationRequest(string? Cnic, string? FullName, DateOnly? DateOfBirth);

/// <summary>NADRA's answer.</summary>
/// <param name="Result">"Verified", "NotFound" or "Mismatch".</param>
/// <param name="Reference">NADRA's transaction reference when verified.</param>
/// <param name="NameMatchScore">How alike the names were (0-1), when there was a record to compare with.</param>
public sealed record NadraVerificationResult(string Result, string? Reference, double? NameMatchScore);

/// <summary>A citizen record added through the control API.</summary>
public sealed record Citizen(string FullName, DateOnly DateOfBirth, string Status = "Active");

/// <summary>
/// Mock NADRA biometric-less verification (M5.5):
/// <list type="bullet">
/// <item>POST /nadra/v1/verifications             CNIC + name + date of birth → Verified / NotFound / Mismatch</item>
/// <item>PUT  /admin/nadra/citizens/{cnic}        add or change a record (control API)</item>
/// <item>GET  /admin/nadra/citizens/{cnic}        read it back</item>
/// </list>
/// The same test prefixes as Identity's built-in stand-in, so the UI behaves the same either way:
/// <list type="table">
/// <item><term>70000-…</term><description>NotFound</description></item>
/// <item><term>71111-…</term><description>Mismatch</description></item>
/// <item><term>72222-…</term><description>NADRA unavailable (503)</description></item>
/// </list>
/// Other CNICs: a stored record must match (name within <see cref="NameMatcher.Threshold"/>, exact date of
/// birth; a Cancelled record is NotFound); without a record the CNIC verifies when
/// Nadra:VerifyUnknownCitizens is on (the default). Faults: "nadra".
/// </summary>
public static class NadraEndpoints
{
    public const string NotFoundPrefix = "70000";
    public const string MismatchPrefix = "71111";
    public const string UnavailablePrefix = "72222";

    public static IEndpointRouteBuilder MapNadraEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/nadra/v1").WithTags("NADRA").RequireClientKey();

        group.MapPost("/verifications", (NadraVerificationRequest request, HttpContext context, FaultInjector faults, MockDatabase database, MockProvidersOptions options) =>
        {
            if (NormalizeCnic(request.Cnic) is not { } cnic)
            {
                return Task.FromResult(Error.Validation("nadra.invalid_cnic", "A CNIC is 13 digits (xxxxx-xxxxxxx-x).").ToProblem());
            }

            if (string.IsNullOrWhiteSpace(request.FullName) || request.DateOfBirth is null)
            {
                return Task.FromResult(Error.Validation("nadra.invalid_request", "fullName and dateOfBirth are required.").ToProblem());
            }

            return faults.RunAsync(context, [Providers.Nadra], async () =>
            {
                switch (cnic[..5])
                {
                    case NotFoundPrefix:
                        return Results.Ok(new NadraVerificationResult("NotFound", null, null));
                    case MismatchPrefix:
                        return Results.Ok(new NadraVerificationResult("Mismatch", null, null));
                    case UnavailablePrefix:
                        return Error.Unavailable("provider.unavailable", "NADRA is unavailable.").ToProblem();
                }

                await using var connection = await database.OpenAsync(context.RequestAborted);
                var record = await connection.QuerySingleOrDefaultAsync<CitizenRow>(new CommandDefinition(
                    "SELECT full_name AS FullName, date_of_birth AS DateOfBirth, status AS Status FROM mock.citizens WHERE cnic = @Cnic",
                    new { Cnic = cnic },
                    cancellationToken: context.RequestAborted));

                if (record is null)
                {
                    return Results.Ok(options.Nadra.VerifyUnknownCitizens
                        ? new NadraVerificationResult("Verified", NewReference(), null)
                        : new NadraVerificationResult("NotFound", null, null));
                }

                if (record.Status != "Active")
                {
                    return Results.Ok(new NadraVerificationResult("NotFound", null, null));
                }

                var score = Math.Round(NameMatcher.Score(record.FullName, request.FullName), 3);
                var dateMatches = DateOnly.FromDateTime(record.DateOfBirth) == request.DateOfBirth;
                return Results.Ok(score >= NameMatcher.Threshold && dateMatches
                    ? new NadraVerificationResult("Verified", NewReference(), score)
                    : new NadraVerificationResult("Mismatch", null, score));
            });
        });

        var admin = endpoints.MapGroup("/admin/nadra/citizens").WithTags("Control: NADRA").RequireAdminKey();

        admin.MapPut("/{cnic}", async (string cnic, Citizen citizen, MockDatabase database, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (NormalizeCnic(cnic) is not { } normalized)
            {
                return Error.Validation("nadra.invalid_cnic", "A CNIC is 13 digits.").ToProblem();
            }

            if (string.IsNullOrWhiteSpace(citizen.FullName) || citizen.Status is not ("Active" or "Cancelled"))
            {
                return Error.Validation("nadra.invalid_citizen", "fullName is required; status is Active or Cancelled.").ToProblem();
            }

            await using var connection = await database.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO mock.citizens (cnic, full_name, date_of_birth, status, updated_at)
                VALUES (@Cnic, @FullName, @DateOfBirth, @Status, @Now)
                ON CONFLICT (cnic) DO UPDATE SET full_name = EXCLUDED.full_name, date_of_birth = EXCLUDED.date_of_birth,
                    status = EXCLUDED.status, updated_at = EXCLUDED.updated_at
                """,
                new { Cnic = normalized, citizen.FullName, DateOfBirth = citizen.DateOfBirth.ToDateTime(TimeOnly.MinValue), citizen.Status, Now = clock.GetUtcNow().UtcDateTime },
                cancellationToken: cancellationToken));
            return Results.Ok(citizen);
        });

        admin.MapGet("/{cnic}", async (string cnic, MockDatabase database, CancellationToken cancellationToken) =>
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            var record = await connection.QuerySingleOrDefaultAsync<CitizenRow>(new CommandDefinition(
                "SELECT full_name AS FullName, date_of_birth AS DateOfBirth, status AS Status FROM mock.citizens WHERE cnic = @Cnic",
                new { Cnic = NormalizeCnic(cnic) ?? "" },
                cancellationToken: cancellationToken));
            return record is null
                ? Error.NotFound("nadra.citizen_not_found", "No record for this CNIC.").ToProblem()
                : Results.Ok(new Citizen(record.FullName, DateOnly.FromDateTime(record.DateOfBirth), record.Status));
        });

        return endpoints;
    }

    public static string? NormalizeCnic(string? input)
    {
        var digits = input?.Replace("-", "", StringComparison.Ordinal).Trim();
        return digits is { Length: 13 } && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    private static string NewReference() => "NADRA-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));

    private sealed class CitizenRow
    {
        public string FullName { get; init; } = "";

        public DateTime DateOfBirth { get; init; }

        public string Status { get; init; } = "";
    }
}
