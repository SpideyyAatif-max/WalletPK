using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Time;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>M12.4–M12.7: monthly statements (and their PDF), the daily report, business figures and the audit viewer's feed.</summary>
[Collection(LedgerCollection.Name)]
public sealed class ReportTests(LedgerApiFactory factory)
{
    private static string ThisMonth => TimeProvider.System.GetPakistanNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string Today => TimeProvider.System.GetBusinessDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private HttpClient Staff(string role = "ops") => factory.CreateClientFor(UserId.New(), null, role);

    [Fact]
    public async Task A_months_statement_adds_up_from_the_opening_to_the_closing_balance()
    {
        var sender = await factory.CustomerAsync(1_000);
        var recipient = await factory.CustomerAsync();
        using (var sent = await sender.SendAsync(recipient.Phone, 25_000, NewKey()))
        {
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await sender.WaitForHistoryAsync(2);
        var statement = await sender.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/wallets/me/statements/{ThisMonth}", UriKind.Relative), Cancel);

        Paisa(statement, "opening").ShouldBe(0);
        Paisa(statement, "moneyIn").ShouldBe(100_000);
        var entries = statement.GetProperty("entries").EnumerateArray().ToList();
        entries.Count.ShouldBe(2);
        entries[0].GetProperty("type").GetString().ShouldBe("CashIn");
        entries[1].GetProperty("type").GetString().ShouldBe("P2PTransfer");
        Paisa(entries[1], "debit").ShouldBeGreaterThanOrEqualTo(25_000);
        Paisa(statement, "moneyOut").ShouldBe(Paisa(entries[1], "debit"));
        Paisa(statement, "closing").ShouldBe(Paisa(statement, "opening") + Paisa(statement, "moneyIn") - Paisa(statement, "moneyOut"));
        Paisa(statement, "closing").ShouldBe(await factory.BalanceAsync(sender.WalletId));
        statement.GetProperty("complete").GetBoolean().ShouldBeFalse();
        statement.GetProperty("walletNumber").GetString().ShouldBe(sender.Phone);
    }

    [Fact]
    public async Task The_statement_downloads_as_a_PDF()
    {
        var customer = await factory.CustomerAsync(500);
        await customer.WaitForHistoryAsync(1);

        using var response = await customer.Client.GetAsync(new Uri($"/api/wallets/me/statements/{ThisMonth}.pdf", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe($"walletpk-statement-{ThisMonth}.pdf");
        var bytes = await response.Content.ReadAsByteArrayAsync(Cancel);
        bytes.Length.ShouldBeGreaterThan(1_000);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).ShouldBe("%PDF-");
    }

    [Fact]
    public async Task An_earlier_month_carries_no_entries_and_a_month_not_started_is_refused()
    {
        var customer = await factory.CustomerAsync(100);
        var last = TimeProvider.System.GetPakistanNow().AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var next = TimeProvider.System.GetPakistanNow().AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

        var earlier = await customer.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/wallets/me/statements/{last}", UriKind.Relative), Cancel);
        earlier.GetProperty("entries").GetArrayLength().ShouldBe(0);
        earlier.GetProperty("complete").GetBoolean().ShouldBeTrue();

        foreach (var month in new[] { next, "2026-13", "november" })
        {
            using var refused = await customer.Client.GetAsync(new Uri($"/api/wallets/me/statements/{month}", UriKind.Relative), Cancel);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await JsonAsync(refused)).GetProperty("code").GetString().ShouldBe("wallet.statement_month_invalid");
        }
    }

    [Fact]
    public async Task The_daily_report_has_the_days_figures_and_the_checks()
    {
        var sender = await factory.CustomerAsync(2_000);
        var recipient = await factory.CustomerAsync();
        using (var sent = await sender.SendAsync(recipient.Phone, 50_000, NewKey()))
        {
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var ops = Staff();
        var report = await ops.GetFromJsonAsync<JsonElement>(new Uri($"/api/ledger/reports/daily?date={Today}", UriKind.Relative), Cancel);

        report.GetProperty("date").GetString().ShouldBe(Today);
        var totals = report.GetProperty("totals");
        var journals = totals.GetProperty("journals").EnumerateArray().ToDictionary(item => item.GetProperty("type").GetString()!);
        journals["P2PTransfer"].GetProperty("count").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
        journals["CashIn"].GetProperty("volumePaisa").GetInt64().ShouldBeGreaterThanOrEqualTo(200_000);
        totals.GetProperty("journalCount").GetInt64().ShouldBe(journals.Values.Sum(item => item.GetProperty("count").GetInt64()));
        totals.GetProperty("walletsOpened").GetInt64().ShouldBeGreaterThanOrEqualTo(2);
        totals.GetProperty("activeWallets").GetInt64().ShouldBeGreaterThanOrEqualTo(2);
        totals.GetProperty("feeIncomePaisa").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        report.GetProperty("balances").EnumerateArray().Select(item => item.GetProperty("type").GetString()).ShouldContain("CustomerWallet");
        report.GetProperty("invariants").GetProperty("healthy").GetBoolean().ShouldBeTrue();
        report.GetProperty("auditChain").TryGetProperty("intact", out _).ShouldBeTrue();

        using var customer = factory.CreateClientFor(UserId.New(), NewPhone());
        (await customer.GetAsync(new Uri("/api/ledger/reports/daily", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ops.GetAsync(new Uri($"/api/ledger/reports/daily?date={TimeProvider.System.GetBusinessDate().AddDays(2):yyyy-MM-dd}", UriKind.Relative), Cancel))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Business_figures_come_per_day_and_as_CSV()
    {
        await factory.CustomerAsync(300);
        using var finance = Staff("finance");
        var from = TimeProvider.System.GetBusinessDate().AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var days = await finance.GetFromJsonAsync<JsonElement>(new Uri($"/api/ledger/reports/business?from={from}&to={Today}", UriKind.Relative), Cancel);
        days.GetArrayLength().ShouldBe(7);
        days.EnumerateArray().Last().GetProperty("date").GetString().ShouldBe(Today);
        days.EnumerateArray().Last().GetProperty("journalCount").GetInt64().ShouldBeGreaterThan(0);

        using var csv = await finance.GetAsync(new Uri($"/api/ledger/reports/business.csv?from={from}&to={Today}", UriKind.Relative), Cancel);
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        var lines = (await csv.Content.ReadAsStringAsync(Cancel)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldStartWith("date,journals,volume_paisa,fee_income_paisa,tax_collected_paisa");
        lines[0].ShouldContain("CashIn_count");
        lines.Length.ShouldBe(8);
        lines[^1].ShouldStartWith(Today);

        (await finance.GetAsync(new Uri($"/api/ledger/reports/business?from={Today}&to={from}", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_audit_feed_is_newest_first_and_pages_back()
    {
        await factory.CustomerAsync(100);
        await factory.SealAuditAsync();
        using var ops = Staff();

        var first = await ops.GetFromJsonAsync<JsonElement>(new Uri("/api/ledger/audit/recent?limit=3", UriKind.Relative), Cancel);
        var ids = first.EnumerateArray().Select(row => row.GetProperty("id").GetInt64()).ToList();
        ids.Count.ShouldBe(3);
        ids.ShouldBe(ids.OrderByDescending(id => id));
        first[0].GetProperty("eventType").GetString().ShouldNotBeNullOrEmpty();

        var oldest = ids[^1];
        var next = await ops.GetFromJsonAsync<JsonElement>(new Uri($"/api/ledger/audit/recent?before={oldest}&limit=3", UriKind.Relative), Cancel);
        next.EnumerateArray().Select(row => row.GetProperty("id").GetInt64()).ShouldAllBe(id => id < oldest);
    }
}
