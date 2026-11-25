using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Reconciliation.IntegrationTests.Infrastructure;
using static WalletPK.Reconciliation.IntegrationTests.Infrastructure.ReconSteps;

namespace WalletPK.Reconciliation.IntegrationTests;

/// <summary>M12.1–M12.3 end to end: real settlement files, real events, the API and the audit chain.</summary>
[Collection(ReconciliationCollection.Name)]
public sealed class ReconciliationTests(ReconciliationFactory factory)
{
    /// <summary>
    /// Scenario S8: the network's file has mismatches of every kind planted in it; reconciliation finds exactly
    /// the ones the providers' answer key lists, each classified correctly, and matches everything else.
    /// </summary>
    [Fact]
    public async Task S8_every_planted_mismatch_is_found_and_classified()
    {
        var topUps = new List<TelcoTopUp>();
        for (var index = 0; index < 14; index++)
        {
            topUps.Add(await factory.Providers.TopUpAsync("JAZZ", "0301", (index + 1) * 10_000));
        }

        await factory.TrackAsync([.. topUps.Select(topUp => Changed(topUp, "JAZZ"))]);
        await factory.Providers.PlantAsync("telcos", "JAZZ", Today, new { missing = 2, duplicate = 2, amountMismatch = 3, unknown = 2 });
        var key = await factory.Providers.InjectedAsync("telcos", "JAZZ", Today);

        var detail = await factory.ReconcileAsync("telco:JAZZ");

        var byReference = topUps.ToDictionary(topUp => topUp.TelcoReference, topUp => topUp.TopUpId.ToString());
        Keys(detail, "MissingAtProvider").ShouldBe(Strings(key.GetProperty("missing")).Select(reference => byReference[reference]).Order(StringComparer.Ordinal));
        Keys(detail, "Duplicate").ShouldBe(Strings(key.GetProperty("duplicate")));
        Keys(detail, "UnknownToUs").ShouldBe(key.GetProperty("unknown").EnumerateArray().Select(line => line.GetProperty("providerReference").GetString()!));

        var amounts = detail.Breaks("AmountMismatch").ToList();
        amounts.Select(item => item.GetProperty("key").GetString()).ShouldBe(key.GetProperty("amountMismatch").EnumerateArray().Select(change => change.GetProperty("providerReference").GetString()));
        foreach (var (found, planted) in amounts.Zip(key.GetProperty("amountMismatch").EnumerateArray()))
        {
            found.GetProperty("ourAmountPaisa").GetInt64().ShouldBe(planted.GetProperty("actualPaisa").GetInt64());
            found.GetProperty("providerAmountPaisa").GetInt64().ShouldBe(planted.GetProperty("reportedPaisa").GetInt64());
        }

        detail.Breaks("Duplicate").ShouldAllBe(item => item.GetProperty("linesInFile").GetInt32() == 2);
        detail.GetProperty("breaks").GetArrayLength().ShouldBe(9);

        var run = detail.GetProperty("run");
        run.GetProperty("status").GetString().ShouldBe("Completed");
        run.GetProperty("matched").GetInt32().ShouldBe(14 - 2 - 2 - 3);
        run.GetProperty("lines").GetInt32().ShouldBe(14 - 2 + 2 + 2);
        run.GetProperty("ourCount").GetInt32().ShouldBe(14);
        run.GetProperty("ourTotalPaisa").GetInt64().ShouldBe(topUps.Sum(topUp => topUp.AmountPaisa));
        run.GetProperty("missingAtProvider").GetInt32().ShouldBe(2);
        run.GetProperty("breaks").GetInt32().ShouldBe(9);
    }

    [Fact]
    public async Task A_settled_item_that_failed_or_is_still_in_flight_here_is_flagged_and_cleared_once_it_completes()
    {
        var done = await factory.Providers.TopUpAsync("ZONG", "0311", 20_000);
        var refunded = await factory.Providers.TopUpAsync("ZONG", "0311", 30_000);
        var stuck = await factory.Providers.TopUpAsync("ZONG", "0311", 40_000);
        var neverArrived = new TelcoTopUp(Guid.NewGuid(), "ZONGNEVERSENT", 50_000, "03110000000");
        await factory.TrackAsync(Changed(done, "ZONG"), Changed(refunded, "ZONG", "Failed"), Changed(stuck, "ZONG", "UnderReview"), Changed(neverArrived, "ZONG"));

        var first = await factory.ReconcileAsync("telco:ZONG");

        first.GetProperty("run").GetProperty("matched").GetInt32().ShouldBe(1);
        first.Breaks("SettledButFailedHere").ShouldHaveSingleItem().GetProperty("ourId").GetString().ShouldBe(refunded.TopUpId.ToString());
        var pending = first.Breaks("SettledButPendingHere").ShouldHaveSingleItem();
        pending.GetProperty("ourStatus").GetString().ShouldBe("Pending");
        first.Breaks("MissingAtProvider").ShouldHaveSingleItem().GetProperty("key").GetString().ShouldBe(neverArrived.TopUpId.ToString());

        // The stuck top-up is confirmed: run again. The same breaks keep their IDs; the cleared one says so.
        await factory.TrackAsync(Changed(stuck, "ZONG"));
        var second = await factory.ReconcileAsync("telco:ZONG");

        second.GetProperty("run").GetProperty("matched").GetInt32().ShouldBe(2);
        var cleared = second.GetProperty("breaks").EnumerateArray().Single(item => item.GetProperty("id").GetString() == pending.GetProperty("id").GetString());
        cleared.GetProperty("status").GetString().ShouldBe("Cleared");
        second.Breaks("SettledButFailedHere").Single().GetProperty("id").GetString()
            .ShouldBe(first.Breaks("SettledButFailedHere").Single().GetProperty("id").GetString());
        second.GetProperty("run").GetProperty("breaks").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Finance_disputes_and_resolves_a_break_and_every_step_is_on_the_audit_chain()
    {
        var lost = new TelcoTopUp(Guid.NewGuid(), "TELENORLOST", 25_000, "03410000000");
        await factory.TrackAsync(Changed(lost, "TELENOR"));
        var detail = await factory.ReconcileAsync("telco:TELENOR");
        var id = detail.Breaks("MissingAtProvider").Single(item => item.GetProperty("key").GetString() == lost.TopUpId.ToString()).GetProperty("id").GetString();

        var financeUser = Guid.NewGuid();
        using var finance = factory.ClientFor("finance", financeUser);
        using var ops = factory.ClientFor("ops");

        using (var forbidden = await ops.PostAsJsonAsync(new Uri($"/api/reconciliation/breaks/{id}/dispute", UriKind.Relative), new { note = "Asked Telenor" }, Cancel))
        {
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var noNote = await finance.PostAsJsonAsync(new Uri($"/api/reconciliation/breaks/{id}/dispute", UriKind.Relative), new { note = "" }, Cancel))
        {
            noNote.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var disputed = await finance.PostAsJsonAsync(new Uri($"/api/reconciliation/breaks/{id}/dispute", UriKind.Relative), new { note = "Raised with Telenor settlement desk, ticket 4471" }, Cancel))
        {
            disputed.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await disputed.JsonAsync()).GetProperty("status").GetString().ShouldBe("Disputed");
        }

        using (var unknown = await finance.PostAsJsonAsync(new Uri($"/api/reconciliation/breaks/{id}/resolve", UriKind.Relative), new { resolution = "Ignored", note = "Not a real resolution" }, Cancel))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var resolved = await finance.PostAsJsonAsync(new Uri($"/api/reconciliation/breaks/{id}/resolve", UriKind.Relative), new { resolution = "Recovered", note = "Telenor paid it in the next day's batch" }, Cancel))
        {
            resolved.StatusCode.ShouldBe(HttpStatusCode.OK);
            var item = await resolved.JsonAsync();
            item.GetProperty("status").GetString().ShouldBe("Resolved");
            item.GetProperty("resolution").GetString().ShouldBe("Recovered");
            item.GetProperty("updatedBy").GetString().ShouldBe($"user:{financeUser}");
        }

        using (var again = await finance.PostAsJsonAsync(new Uri($"/api/reconciliation/breaks/{id}/resolve", UriKind.Relative), new { resolution = "WrittenOff", note = "Second thoughts" }, Cancel))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        // A run again does not reopen a resolved break.
        var rerun = await factory.ReconcileAsync("telco:TELENOR");
        rerun.GetProperty("breaks").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id).GetProperty("status").GetString().ShouldBe("Resolved");

        var view = await ops.GetFromJsonAsync<JsonElement>(new Uri($"/api/reconciliation/breaks/{id}", UriKind.Relative), Cancel);
        view.GetProperty("payment").GetProperty("id").GetString().ShouldBe(lost.TopUpId.ToString());
        view.GetProperty("history").EnumerateArray().Select(entry => entry.GetProperty("action").GetString()).ShouldBe(["break.disputed", "break.resolved"]);

        var trail = await ops.GetFromJsonAsync<JsonElement>(new Uri("/api/reconciliation/audit?limit=1000", UriKind.Relative), Cancel);
        trail.EnumerateArray().Select(entry => entry.GetProperty("action").GetString()).ShouldContain("run.completed");
        var verify = await ops.GetFromJsonAsync<JsonElement>(new Uri("/api/reconciliation/audit/verify", UriKind.Relative), Cancel);
        verify.GetProperty("intact").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Changing_an_audit_entry_breaks_the_chain_there()
    {
        var detail = await factory.ReconcileAsync("biller:PTCL");
        detail.GetProperty("run").GetProperty("status").GetString().ShouldBe("Completed");
        using var ops = factory.ClientFor("ops");
        var trail = await ops.GetFromJsonAsync<JsonElement>(new Uri("/api/reconciliation/audit?limit=1000", UriKind.Relative), Cancel);
        var entry = trail.EnumerateArray().Last();
        var sequence = entry.GetProperty("sequence").GetInt64();
        var actor = entry.GetProperty("actor").GetString();

        await factory.ExecuteAsync($"UPDATE audit_log SET actor = 'user:someone-else' WHERE sequence = {sequence}");
        try
        {
            var verify = await ops.GetFromJsonAsync<JsonElement>(new Uri("/api/reconciliation/audit/verify", UriKind.Relative), Cancel);
            verify.GetProperty("intact").GetBoolean().ShouldBeFalse();
            verify.GetProperty("brokenAt").GetInt64().ShouldBe(sequence);
        }
        finally
        {
            await factory.ExecuteAsync($"UPDATE audit_log SET actor = '{actor}' WHERE sequence = {sequence}");
        }

        (await ops.GetFromJsonAsync<JsonElement>(new Uri("/api/reconciliation/audit/verify", UriKind.Relative), Cancel)).GetProperty("intact").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_file_that_is_not_there_fails_the_run_and_the_day_shows_it()
    {
        var detail = await factory.ReconcileAsync(ReconciliationFactory.NoFileParty);

        var run = detail.GetProperty("run");
        run.GetProperty("status").GetString().ShouldBe("Failed");
        run.GetProperty("problem").GetString().ShouldNotBeNull().ShouldContain("404");

        using var ops = factory.ClientFor("ops");
        var summary = await ops.GetFromJsonAsync<JsonElement>(new Uri($"/api/reconciliation/summary?date={Today:yyyy-MM-dd}", UriKind.Relative), Cancel);
        summary.GetProperty("parties").EnumerateArray().Single(party => party.GetProperty("party").GetString() == ReconciliationFactory.NoFileParty)
            .GetProperty("run").GetProperty("status").GetString().ShouldBe("Failed");
        var days = await ops.GetFromJsonAsync<JsonElement>(new Uri("/api/reconciliation/days", UriKind.Relative), Cancel);
        days.EnumerateArray().First().GetProperty("date").GetString().ShouldBe(Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_fetched_file_is_kept_as_evidence()
    {
        var topUp = await factory.Providers.TopUpAsync("UFONE", "0331", 15_000);
        await factory.TrackAsync(Changed(topUp, "UFONE"));
        var detail = await factory.ReconcileAsync("telco:UFONE");

        using var ops = factory.ClientFor("ops");
        var csv = await ops.GetStringAsync(new Uri($"/api/reconciliation/runs/{detail.GetProperty("run").GetProperty("id").GetString()}/file", UriKind.Relative), Cancel);

        csv.ShouldStartWith($"H,telco:UFONE,{Today:yyyy-MM-dd}");
        csv.ShouldContain(topUp.TelcoReference);
    }

    [Fact]
    public async Task Runs_are_for_finance_and_for_days_that_have_ended()
    {
        using var ops = factory.ClientFor("ops");
        using var customer = factory.ClientFor("customer");
        using var finance = factory.ClientFor("finance");

        (await ops.StartAsync(Today, "bank:HBL")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await customer.GetAsync(new Uri("/api/reconciliation/breaks", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await finance.StartAsync(Today.AddDays(1), "bank:HBL")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await finance.StartAsync(Today, "bank:NOWHERE")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await finance.GetAsync(new Uri("/api/reconciliation/parties", UriKind.Relative), Cancel)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static IEnumerable<string> Keys(JsonElement detail, string kind) => detail.Breaks(kind).Select(item => item.GetProperty("key").GetString()!);

    private static IEnumerable<string> Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!);
}
