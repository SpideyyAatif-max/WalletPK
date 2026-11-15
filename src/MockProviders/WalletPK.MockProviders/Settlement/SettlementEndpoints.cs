using System.Globalization;
using Dapper;
using WalletPK.Common.Results;
using WalletPK.MockProviders.Banks;
using WalletPK.MockProviders.Billers;
using WalletPK.MockProviders.Common;
using WalletPK.MockProviders.Faults;
using WalletPK.MockProviders.Security;
using WalletPK.MockProviders.Storage;
using WalletPK.MockProviders.Telcos;
using WalletPK.Web.Errors;

namespace WalletPK.MockProviders.Settlement;

/// <summary>
/// Settlement files (M5.6):
/// <list type="bullet">
/// <item>GET    /settlement/{kind}/{code}/{date}                         the CSV (kind: banks, billers, telcos; date: yyyy-MM-dd, Pakistan time)</item>
/// <item>PUT    /admin/settlement/{kind}/{code}/{date}/mismatches       plant mismatches, e.g. {"missing": 2, "duplicate": 1, "amountMismatch": 1, "unknown": 1}</item>
/// <item>DELETE /admin/settlement/{kind}/{code}/{date}/mismatches       a clean file again</item>
/// <item>GET    /admin/settlement/{kind}/{code}/{date}/injected         the answer key for the file as it is now</item>
/// </list>
/// A bank's file lists the Raast transfers it settled; a biller's the bills paid; a telco's the top-ups.
/// </summary>
public static class SettlementEndpoints
{
    public static IEndpointRouteBuilder MapSettlementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/settlement").WithTags("Settlement files").RequireClientKey()
            .MapGet("/{kind}/{code}/{date}", async (string kind, string code, string date, OperationStore store, MockDatabase database, CancellationToken cancellationToken) =>
            {
                if (Resolve(kind, code, date) is not { } target)
                {
                    return NotFound(kind, code, date);
                }

                var (lines, _) = await BuildAsync(target.Party, target.Date, store, database, cancellationToken);
                return Results.Text(SettlementFile.ToCsv(target.Party, target.Date, lines), "text/csv; charset=utf-8");
            });

        var admin = endpoints.MapGroup("/admin/settlement").WithTags("Control: settlement").RequireAdminKey();

        admin.MapPut("/{kind}/{code}/{date}/mismatches", async (string kind, string code, string date, MismatchPlan plan, MockDatabase database, CancellationToken cancellationToken) =>
        {
            if (Resolve(kind, code, date) is not { } target)
            {
                return NotFound(kind, code, date);
            }

            if (!plan.IsValid())
            {
                return Error.Validation("settlement.invalid_plan", "Counts must be zero or more (at most 10,000 in total).").ToProblem();
            }

            await using var connection = await database.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO mock.settlement_plans (party, business_date, missing, duplicate, amount_mismatch, unknown)
                VALUES (@Party, @Date, @Missing, @Duplicate, @AmountMismatch, @Unknown)
                ON CONFLICT (party, business_date) DO UPDATE SET missing = EXCLUDED.missing, duplicate = EXCLUDED.duplicate,
                    amount_mismatch = EXCLUDED.amount_mismatch, unknown = EXCLUDED.unknown
                """,
                new { target.Party, Date = target.Date.ToDateTime(TimeOnly.MinValue), plan.Missing, plan.Duplicate, plan.AmountMismatch, plan.Unknown },
                cancellationToken: cancellationToken));
            return Results.Ok(plan);
        });

        admin.MapDelete("/{kind}/{code}/{date}/mismatches", async (string kind, string code, string date, MockDatabase database, CancellationToken cancellationToken) =>
        {
            if (Resolve(kind, code, date) is not { } target)
            {
                return NotFound(kind, code, date);
            }

            await using var connection = await database.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM mock.settlement_plans WHERE party = @Party AND business_date = @Date",
                new { target.Party, Date = target.Date.ToDateTime(TimeOnly.MinValue) },
                cancellationToken: cancellationToken));
            return Results.NoContent();
        });

        admin.MapGet("/{kind}/{code}/{date}/injected", async (string kind, string code, string date, OperationStore store, MockDatabase database, CancellationToken cancellationToken) =>
        {
            if (Resolve(kind, code, date) is not { } target)
            {
                return NotFound(kind, code, date);
            }

            var (_, injected) = await BuildAsync(target.Party, target.Date, store, database, cancellationToken);
            return Results.Ok(injected);
        });

        return endpoints;
    }

    private static async Task<(IReadOnlyList<SettlementLine> Lines, InjectedMismatches Injected)> BuildAsync(
        string party, DateOnly date, OperationStore store, MockDatabase database, CancellationToken cancellationToken)
    {
        var start = PakistanTime.StartOf(date);
        var settled = await store.CompletedForSettlementAsync(party, start, start.AddDays(1), cancellationToken);
        var actual = settled.Select(operation => new SettlementLine(
            operation.Reference,
            operation.ClientId,
            operation.AmountPaisa,
            new DateTimeOffset(DateTime.SpecifyKind(operation.CompletedAt!.Value, DateTimeKind.Utc)))).ToList();

        await using var connection = await database.OpenAsync(cancellationToken);
        var plan = await connection.QuerySingleOrDefaultAsync<MismatchPlan>(new CommandDefinition(
            """
            SELECT missing AS Missing, duplicate AS Duplicate, amount_mismatch AS AmountMismatch, unknown AS Unknown
            FROM mock.settlement_plans WHERE party = @Party AND business_date = @Date
            """,
            new { Party = party, Date = date.ToDateTime(TimeOnly.MinValue) },
            cancellationToken: cancellationToken)) ?? new MismatchPlan();

        return SettlementFile.Apply(actual, plan, party, date);
    }

    private static (string Party, DateOnly Date)? Resolve(string kind, string code, string date)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var businessDate))
        {
            return null;
        }

        return kind.ToUpperInvariant() switch
        {
            "BANKS" when BankDirectory.ByCode(code) is { } bank => (Providers.Bank(bank.Code), businessDate),
            "BILLERS" when BillerCatalog.ByCode(code) is { } biller => (Providers.Biller(biller.Code), businessDate),
            "TELCOS" when TelcoCatalog.ByCode(code) is { } telco => (Providers.Telco(telco.Code), businessDate),
            _ => null,
        };
    }

    private static IResult NotFound(string kind, string code, string date) =>
        Error.NotFound("settlement.not_found", $"No settlement file '{kind}/{code}/{date}'. Kinds: banks, billers, telcos; dates as yyyy-MM-dd.").ToProblem();
}
