using System.Globalization;
using System.Net;
using System.Text.Json;
using WalletPK.Common.Time;
using WalletPK.Contracts.Fraud.V1;
using WalletPK.Fraud.IntegrationTests.Infrastructure;
using static WalletPK.Fraud.IntegrationTests.Infrastructure.FraudSteps;

namespace WalletPK.Fraud.IntegrationTests;

/// <summary>M12.6, M12.7: suspicious-activity exports, a case written up for filing, and the audit viewer's feed.</summary>
[Collection(FraudCollection.Name)]
public sealed class ReportTests(FraudFactory factory)
{
    private static string Today => TimeProvider.System.GetBusinessDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A new device and three big payments to strangers: blocked, with a case.</summary>
    private async Task<(TestUser User, Guid CaseId)> BlockedAsync()
    {
        var user = await factory.UserAsync(accountDays: 400, deviceHours: 0.5);
        await factory.ScoreAsync(user, 400_000, NewNumber());
        await factory.ScoreAsync(user, 400_000, NewNumber());
        (await factory.ScoreAsync(user, 400_000, NewNumber())).Action.ShouldBe(RiskAction.Block);
        using var compliance = factory.ClientFor("compliance");
        var alerts = await GetAsync(compliance, $"/api/fraud/alerts?userId={user.Id}");
        return (user, alerts.EnumerateArray().First(alert => alert.GetProperty("action").GetString() == "Block").GetProperty("caseId").GetGuid());
    }

    [Fact]
    public async Task The_periods_alerts_export_as_CSV_with_their_cases_and_the_export_is_audited()
    {
        var (user, caseId) = await BlockedAsync();
        var officer = Guid.NewGuid();
        using var compliance = factory.ClientFor("compliance", officer);

        using var response = await compliance.GetAsync(new Uri($"/api/fraud/reports/suspicious.csv?from={Today}&to={Today}&minSeverity=High", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var lines = (await response.Content.ReadAsStringAsync(Cancel)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldStartWith("alert_id,raised_at,user_id,kind,severity,score,action");
        var mine = lines.Skip(1).Where(line => line.Contains(user.Id.ToString(), StringComparison.Ordinal)).ToList();
        mine.ShouldNotBeEmpty();
        mine.ShouldAllBe(line => line.Contains(",High,", StringComparison.Ordinal));
        var caseNumber = (await GetAsync(compliance, $"/api/fraud/cases/{caseId}")).GetProperty("case").GetProperty("number").GetInt64();
        mine.ShouldContain(line => line.Contains($",{caseNumber},", StringComparison.Ordinal));

        var audit = await GetAsync(compliance, "/api/fraud/audit/recent?limit=20");
        audit.EnumerateArray().ShouldContain(entry => entry.GetProperty("action").GetString() == "report.alerts_exported" && entry.GetProperty("actor").GetString() == $"user:{officer}");
    }

    [Fact]
    public async Task A_case_is_written_up_for_filing()
    {
        var (user, caseId) = await BlockedAsync();
        using var compliance = factory.ClientFor("compliance");
        using (var noted = await PostAsync(compliance, $"/api/fraud/cases/{caseId}/notes", new { text = "Three transfers of Rs. 4,000 to new numbers within minutes of a new device." }))
        {
            noted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var report = await GetAsync(compliance, $"/api/fraud/cases/{caseId}/report");

        report.GetProperty("case").GetProperty("id").GetGuid().ShouldBe(caseId);
        report.GetProperty("subjects").EnumerateArray().Select(subject => subject.GetProperty("userId").GetGuid()).ShouldContain(user.Id);
        var alerts = report.GetProperty("alerts").EnumerateArray().ToList();
        alerts.ShouldNotBeEmpty();
        alerts.Select(alert => alert.GetProperty("createdAt").GetDateTimeOffset()).ShouldBeInOrder();
        report.GetProperty("totalAmountPaisa").GetInt64().ShouldBe(alerts.Sum(alert => alert.GetProperty("amountPaisa").ValueKind == JsonValueKind.Number ? alert.GetProperty("amountPaisa").GetInt64() : 0));
        report.GetProperty("notes").GetArrayLength().ShouldBe(1);

        var audit = await GetAsync(compliance, "/api/fraud/audit/recent?limit=5");
        audit[0].GetProperty("action").GetString().ShouldBe("report.case_exported");
        audit[0].GetProperty("subject").GetString().ShouldBe($"case:{caseId}");
        (await GetAsync(compliance, "/api/fraud/audit/verify")).GetProperty("intact").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Exports_are_for_compliance_and_check_their_range()
    {
        using var ops = factory.ClientFor("ops");
        using var compliance = factory.ClientFor("compliance");

        (await ops.GetAsync(new Uri("/api/fraud/reports/suspicious.csv", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await compliance.GetAsync(new Uri("/api/fraud/reports/suspicious.csv?from=2026-11-10&to=2026-11-01", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await compliance.GetAsync(new Uri("/api/fraud/reports/suspicious.csv?minSeverity=Extreme", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await compliance.GetAsync(new Uri($"/api/fraud/cases/{Guid.NewGuid()}/report", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var page = await GetAsync(compliance, "/api/fraud/audit/recent?limit=3");
        var sequences = page.EnumerateArray().Select(entry => entry.GetProperty("sequence").GetInt64()).ToList();
        sequences.ShouldBe(sequences.OrderByDescending(sequence => sequence));
    }

    [Fact]
    public void CSV_fields_are_quoted_and_cannot_smuggle_formulas()
    {
        Application.SuspiciousActivityService.Field("plain").ShouldBe("plain");
        Application.SuspiciousActivityService.Field("a,b").ShouldBe("\"a,b\"");
        Application.SuspiciousActivityService.Field("say \"hi\"").ShouldBe("\"say \"\"hi\"\"\"");
        Application.SuspiciousActivityService.Field("=HYPERLINK(1)").ShouldBe("'=HYPERLINK(1)");
        Application.SuspiciousActivityService.Field(null).ShouldBe("");
    }
}
