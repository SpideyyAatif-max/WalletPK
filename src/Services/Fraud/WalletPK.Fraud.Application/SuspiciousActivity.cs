using System.Globalization;
using System.Text;
using System.Text.Json;
using WalletPK.Common.Results;

namespace WalletPK.Fraud.Application;

/// <summary>A case written up for the regulator (a suspicious transaction report, M12.7).</summary>
/// <param name="ReportId">This report's ID (quoted on the filing).</param>
/// <param name="GeneratedAt">When.</param>
/// <param name="GeneratedBy">Who exported it.</param>
/// <param name="Case">The case.</param>
/// <param name="Subjects">Each person in it, with whether their wallets are frozen.</param>
/// <param name="Alerts">Every alert in the case, oldest first.</param>
/// <param name="Notes">The investigation notes, oldest first.</param>
/// <param name="TotalAmountPaisa">What the alerted payments add up to.</param>
/// <param name="FirstActivityAt">The first alert.</param>
/// <param name="LastActivityAt">The last alert.</param>
public sealed record SuspiciousActivityReport(
    Guid ReportId,
    DateTimeOffset GeneratedAt,
    string GeneratedBy,
    FraudCase Case,
    IReadOnlyList<ReportSubject> Subjects,
    IReadOnlyList<FraudAlert> Alerts,
    IReadOnlyList<CaseNote> Notes,
    long TotalAmountPaisa,
    DateTimeOffset? FirstActivityAt,
    DateTimeOffset? LastActivityAt);

/// <param name="UserId">The person.</param>
/// <param name="Frozen">Whether their wallets are frozen now.</param>
/// <param name="FreezeReason">Why, if they are.</param>
public sealed record ReportSubject(Guid UserId, bool Frozen, string? FreezeReason);

/// <summary>
/// Suspicious-activity reporting (M12.7): the alerts of a period as CSV for the compliance team's own review,
/// and one case written up for filing. Every export goes on the audit log: who took what data out, and when.
/// </summary>
public sealed class SuspiciousActivityService(IFraudStore store, TimeProvider timeProvider)
{
    /// <summary>At most this many alerts in one export.</summary>
    public const int MaxAlerts = 50_000;

    private static readonly string[] Severities = ["Low", "Medium", "High"];

    public static Error InvalidRange { get; } =
        Error.Validation("fraud.report_range_invalid", "\"from\" must not be after \"to\", at most 366 days apart.");

    public static Error InvalidSeverity { get; } =
        Error.Validation("fraud.report_severity_invalid", "minSeverity is Low, Medium or High.");

    /// <summary>The alerts raised from <paramref name="from"/> to <paramref name="to"/> (Pakistan days) at <paramref name="minSeverity"/> or above (default Medium).</summary>
    public async Task<Result<string>> AlertsCsvAsync(DateOnly from, DateOnly to, string? minSeverity, string by, CancellationToken cancellationToken)
    {
        if (from > to || to.DayNumber - from.DayNumber > 365)
        {
            return InvalidRange;
        }

        var floor = Array.FindIndex(Severities, severity => string.Equals(severity, minSeverity ?? "Medium", StringComparison.OrdinalIgnoreCase));
        if (floor < 0)
        {
            return InvalidSeverity;
        }

        // PostgreSQL takes timestamps in UTC only.
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5)).ToUniversalTime();
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5)).ToUniversalTime();
        var alerts = await store.AlertsBetweenAsync(start, end, Severities[floor..], MaxAlerts, cancellationToken);
        var cases = (await store.CasesByIdAsync([.. alerts.Select(alert => alert.CaseId).OfType<Guid>().Distinct()], cancellationToken)).ToDictionary(fraudCase => fraudCase.Id);

        var csv = new StringBuilder("alert_id,raised_at,user_id,kind,severity,score,action,rules,transaction_id,transaction_type,amount_paisa,counterparty,case_number,case_status,case_resolution,summary\n");
        foreach (var alert in alerts)
        {
            var fraudCase = alert.CaseId is { } caseId ? cases.GetValueOrDefault(caseId) : null;
            csv.Append(CultureInfo.InvariantCulture,
                $"{alert.Id},{alert.CreatedAt.UtcDateTime:O},{alert.UserId},{alert.Kind},{alert.Severity},{alert.Score},{alert.Action},{Field(string.Join(' ', alert.Rules))},{alert.TransactionId},{alert.TransactionType},{alert.AmountPaisa},{Field(alert.Counterparty)},{fraudCase?.Number},{fraudCase?.Status},{fraudCase?.Resolution},{Field(alert.Summary)}\n");
        }

        await store.AuditAsync(by, "report.alerts_exported", $"report:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}",
            JsonSerializer.Serialize(new { from, to, minSeverity = Severities[floor], alerts = alerts.Count }, JsonSerializerOptions.Web), timeProvider.GetUtcNow(), cancellationToken);
        return csv.ToString();
    }

    /// <summary>One case written up for filing.</summary>
    public async Task<Result<SuspiciousActivityReport>> CaseReportAsync(Guid caseId, string by, CancellationToken cancellationToken)
    {
        if (await store.CaseAsync(caseId, cancellationToken) is not { } fraudCase)
        {
            return FraudErrors.CaseNotFound;
        }

        var alerts = (await store.AlertsAsync(null, caseId, null, 1_000, cancellationToken)).OrderBy(alert => alert.CreatedAt).ToList();
        var notes = await store.NotesAsync(caseId, cancellationToken);
        var subjects = new List<ReportSubject>();
        foreach (var userId in fraudCase.Subjects.Append(fraudCase.UserId).Distinct())
        {
            var flag = await store.FlagAsync(userId, cancellationToken);
            subjects.Add(new ReportSubject(userId, flag?.Frozen ?? false, flag is { Frozen: true } ? flag.Reason : null));
        }

        var now = timeProvider.GetUtcNow();
        var report = new SuspiciousActivityReport(
            Guid.CreateVersion7(now),
            now,
            by,
            fraudCase,
            subjects,
            alerts,
            notes,
            alerts.Sum(alert => alert.AmountPaisa ?? 0),
            alerts.FirstOrDefault()?.CreatedAt,
            alerts.LastOrDefault()?.CreatedAt);
        await store.AuditAsync(by, "report.case_exported", $"case:{fraudCase.Id}",
            JsonSerializer.Serialize(new { reportId = report.ReportId, caseNumber = fraudCase.Number, alerts = alerts.Count }, JsonSerializerOptions.Web), now, cancellationToken);
        return report;
    }

    /// <summary>A CSV field: quoted when it holds a comma, quote or line break; a leading = + - @ is defused (spreadsheet formulas).</summary>
    internal static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var safe = value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return safe.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : safe;
    }
}
