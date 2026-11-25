using System.Globalization;
using System.Text;
using WalletPK.Common.Results;
using WalletPK.Common.Time;
using WalletPK.Ledger.Application.Invariants;
using WalletPK.Ledger.Application.ReadModels;
using WalletPK.Ledger.Application.Reports;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Audit;
using WalletPK.Ledger.Infrastructure.Invariants;
using WalletPK.Web.Authentication;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Ledger.Api.Endpoints;

/// <summary>
/// Back-office checks of the books (ops, compliance, finance and admin roles only):
/// <list type="bullet">
/// <item>GET /api/ledger/invariants   run every invariant check now on one consistent snapshot</item>
/// <item>GET /api/ledger/audit        verify the whole audit hash chain; /audit/recent?before= the rows, newest first</item>
/// <item>GET /api/ledger/reports/daily?date=   the daily report: business figures, balances, invariants and the audit chain (M12.4)</item>
/// <item>GET /api/ledger/reports/business[.csv]?from=&amp;to=   business figures per day (M12.7)</item>
/// <item>GET /api/ledger/metrics      journals and volume per minute (?from=&amp;to=&amp;type=; default the last hour, at most a day)</item>
/// <item>GET  /api/ledger/read-models         how far the read models have caught up with the ledger</item>
/// <item>POST /api/ledger/read-models/rebuild empty the read models and replay ledger.events from the start (admin only)</item>
/// </list>
/// The checks always answer 200; the body's "healthy" / "intact" field carries the verdict.
/// </summary>
public static class LedgerOperationsEndpoints
{
    public static IEndpointRouteBuilder MapLedgerOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ledger").WithTags("Ledger operations").RequireAuthorization(WalletPkPolicies.Operations);

        group.MapGet("/invariants", async (ILedgerInvariantChecker checker, LedgerMetrics metrics, CancellationToken cancellationToken) =>
        {
            var report = await checker.CheckAsync(cancellationToken);
            metrics.Record(report);
            return TypedResults.Ok(report);
        });

        group.MapGet("/audit", async (AuditChainVerifier verifier, CancellationToken cancellationToken) =>
            TypedResults.Ok(await verifier.VerifyAsync(cancellationToken)));

        group.MapGet("/audit/recent", async (long? before, int? limit, AuditTrail trail, CancellationToken cancellationToken) =>
            TypedResults.Ok(await trail.RecentAsync(before, limit, cancellationToken)));

        // ---- Reports (M12.4, M12.7): from the ledger tables, Pakistan days ----
        group.MapGet("/reports/daily", async (
            DateOnly? date, IBusinessReportQueries reports, ILedgerInvariantChecker checker, AuditChainVerifier verifier, LedgerMetrics metrics,
            TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var day = date ?? timeProvider.GetBusinessDate().AddDays(-1);
            if (day > timeProvider.GetBusinessDate())
            {
                return Error.Validation("ledger.report_date_invalid", "The day has not started yet.").ToProblem();
            }

            var totals = (await reports.DaysAsync(day, day, cancellationToken)).Single();
            var invariants = await checker.CheckAsync(cancellationToken);
            metrics.Record(invariants);
            var audit = await verifier.VerifyAsync(cancellationToken);
            var balances = await reports.BalancesAsync(cancellationToken);
            return TypedResults.Ok(new DailyReport(day, timeProvider.GetUtcNow(), totals, balances, invariants, audit));
        }).Produces<DailyReport>();

        group.MapGet("/reports/business", async (DateOnly? from, DateOnly? to, IBusinessReportQueries reports, TimeProvider timeProvider, CancellationToken cancellationToken) =>
            BusinessRange(from, to, timeProvider) is not { } range
                ? Error.Validation("ledger.report_range_invalid", RangeRule).ToProblem()
                : TypedResults.Ok(await reports.DaysAsync(range.From, range.To, cancellationToken)))
            .Produces<IReadOnlyList<DayTotals>>();

        group.MapGet("/reports/business.csv", async (DateOnly? from, DateOnly? to, IBusinessReportQueries reports, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            if (BusinessRange(from, to, timeProvider) is not { } range)
            {
                return Error.Validation("ledger.report_range_invalid", RangeRule).ToProblem();
            }

            var days = await reports.DaysAsync(range.From, range.To, cancellationToken);
            return Results.File(Encoding.UTF8.GetBytes(BusinessCsv(days)), "text/csv; charset=utf-8", $"walletpk-business-{range.From:yyyyMMdd}-{range.To:yyyyMMdd}.csv");
        });

        group.MapGet("/metrics", async (
            DateTimeOffset? from, DateTimeOffset? to, string? type,
            IReadModelQueries queries, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var until = to ?? timeProvider.GetUtcNow();
            var since = from ?? until - TimeSpan.FromHours(1);
            if (since >= until || until - since > _maxMetricsRange)
            {
                return Error.Validation("ledger.metrics_range_invalid", "\"from\" must be before \"to\", at most one day apart.").ToProblem();
            }

            JournalType? journalType = null;
            if (type is { Length: > 0 })
            {
                if (!Enum.TryParse<JournalType>(type, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(type, out _))
                {
                    return Error.Validation("ledger.metrics_type_invalid", $"Unknown type '{type}'.").ToProblem();
                }

                journalType = parsed;
            }

            var minutes = await queries.GetMetricsAsync(since, until, journalType, cancellationToken);
            return TypedResults.Ok(new { from = since, to = until, minutes });
        });

        group.MapGet("/read-models", async (IReadModelMaintenance readModels, CancellationToken cancellationToken) =>
            TypedResults.Ok(await readModels.GetStatusAsync(cancellationToken)));

        group.MapPost("/read-models/rebuild", async (IReadModelMaintenance readModels, CancellationToken cancellationToken) =>
            {
                var generation = await readModels.RebuildAsync(cancellationToken);
                return TypedResults.Accepted("/api/ledger/read-models", new { generation });
            })
            .RequireAuthorization(policy => policy.RequireClaim(WalletPkClaimTypes.Role, "admin"));

        return endpoints;
    }

    private static readonly TimeSpan _maxMetricsRange = TimeSpan.FromDays(1);

    private const string RangeRule = "\"from\" must not be after \"to\", at most 92 days apart, and not in the future.";

    /// <summary>Default: the last 30 days that have ended.</summary>
    private static (DateOnly From, DateOnly To)? BusinessRange(DateOnly? from, DateOnly? to, TimeProvider timeProvider)
    {
        var today = timeProvider.GetBusinessDate();
        var last = to ?? today.AddDays(-1);
        var first = from ?? last.AddDays(-29);
        return first > last || last > today || last.DayNumber - first.DayNumber > 91 ? null : (first, last);
    }

    /// <summary>One row per day; one column pair (count, volume) per journal type seen in the range.</summary>
    internal static string BusinessCsv(IReadOnlyList<DayTotals> days)
    {
        var types = days.SelectMany(day => day.Journals.Select(journal => journal.Type)).Distinct().Order(StringComparer.Ordinal).ToList();
        var csv = new StringBuilder("date,journals,volume_paisa,fee_income_paisa,tax_collected_paisa,commission_paisa,wallets_opened,active_wallets");
        foreach (var type in types)
        {
            csv.Append(CultureInfo.InvariantCulture, $",{type}_count,{type}_volume_paisa");
        }

        csv.Append('\n');
        foreach (var day in days)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{day.Date:yyyy-MM-dd},{day.JournalCount},{day.VolumePaisa},{day.FeeIncomePaisa},{day.TaxCollectedPaisa},{day.CommissionPaisa},{day.WalletsOpened},{day.ActiveWallets}");
            foreach (var type in types)
            {
                var journal = day.Journals.FirstOrDefault(item => item.Type == type);
                csv.Append(CultureInfo.InvariantCulture, $",{journal?.Count ?? 0},{journal?.VolumePaisa ?? 0}");
            }

            csv.Append('\n');
        }

        return csv.ToString();
    }
}

/// <summary>The daily report (M12.4): the day's business figures, what the books hold now, and the checks.</summary>
/// <param name="Date">The day (Pakistan time).</param>
/// <param name="GeneratedAt">When.</param>
/// <param name="Totals">The day's journals, fees, tax and wallets.</param>
/// <param name="Balances">Balances now, by kind of account.</param>
/// <param name="Invariants">Every invariant check, run now.</param>
/// <param name="AuditChain">The audit chain, verified now.</param>
public sealed record DailyReport(DateOnly Date, DateTimeOffset GeneratedAt, DayTotals Totals, IReadOnlyList<BalanceTotal> Balances, InvariantReport Invariants, AuditChainReport AuditChain);
