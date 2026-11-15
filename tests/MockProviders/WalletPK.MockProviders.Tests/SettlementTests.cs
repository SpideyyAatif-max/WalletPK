using System.Net;
using System.Net.Http.Json;
using WalletPK.MockProviders.Tests.Infrastructure;
using static WalletPK.MockProviders.Tests.Infrastructure.TestKit;

namespace WalletPK.MockProviders.Tests;

/// <summary>M5.6: end-of-day settlement files, and mismatches planted in them on purpose.</summary>
[Collection(MockProvidersCollection.Name)]
public sealed class SettlementTests(MockProvidersFactory factory)
{
    private sealed record Line(string Reference, string Client, long Amount);

    private static (List<Line> Lines, string[] Trailer) Parse(string csv)
    {
        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row => row.Split(',')).ToList();
        rows[0][0].ShouldBe("H");
        rows[^1][0].ShouldBe("T");
        var lines = rows[1..^1].Select(row =>
        {
            row[0].ShouldBe("D");
            return new Line(row[1], row[2], long.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture));
        }).ToList();
        return (lines, rows[^1]);
    }

    private async Task<string> FileAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    /// <summary>Pays <paramref name="count"/> PTCL bills today and returns their payment IDs and amounts.</summary>
    private static async Task<Dictionary<string, long>> PayBillsAsync(HttpClient client, int count)
    {
        var paid = new Dictionary<string, long>();
        for (var i = 0; i < count; i++)
        {
            var consumer = "04" + Random.Shared.NextInt64(0, 1_000_000_000).ToString("D9", System.Globalization.CultureInfo.InvariantCulture);
            var amount = (await client.GetJsonAsync($"/billers/PTCL/bills/{consumer}")).GetProperty("payablePaisa").GetInt64();
            var paymentId = NewId();
            using var response = await client.PostJsonAsync("/billers/PTCL/payments", new { paymentId, consumerNumber = consumer, amountPaisa = amount });
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            paid[paymentId] = amount;
        }

        return paid;
    }

    [Fact]
    public async Task A_clean_file_lists_every_settled_item_once_with_a_matching_trailer()
    {
        using var client = factory.Client();
        var paid = await PayBillsAsync(client, 3);

        var (lines, trailer) = Parse(await FileAsync(client, $"/settlement/billers/PTCL/{TodayInPakistan()}"));

        foreach (var (paymentId, amount) in paid)
        {
            lines.Where(line => line.Client == paymentId).ShouldHaveSingleItem().Amount.ShouldBe(amount);
        }

        trailer[1].ShouldBe(lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        trailer[2].ShouldBe(lines.Sum(line => line.Amount).ToString(System.Globalization.CultureInfo.InvariantCulture));
        lines.Select(line => line.Reference).ShouldBeInOrder(SortDirection.Ascending, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Planted_mismatches_are_exactly_the_ones_in_the_answer_key()
    {
        using var client = factory.Client();
        using var admin = factory.Admin();
        var date = TodayInPakistan();
        await PayBillsAsync(client, 8);

        // A raast transfer settles into HBL's file, not the biller's.
        var cleanBiller = Parse(await FileAsync(client, $"/settlement/billers/PTCL/{date}")).Lines;

        using (var planned = await admin.PutAsJsonAsync(new Uri($"/admin/settlement/billers/PTCL/{date}/mismatches", UriKind.Relative), new { missing = 2, duplicate = 1, amountMismatch = 2, unknown = 1 }, Cancel))
        {
            planned.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var csv = await FileAsync(client, $"/settlement/billers/PTCL/{date}");
        (await FileAsync(client, $"/settlement/billers/PTCL/{date}")).ShouldBe(csv, "the same plan gives the same file");
        var file = Parse(csv).Lines;
        var key = await admin.GetJsonAsync($"/admin/settlement/billers/PTCL/{date}/injected");

        var missing = key.GetProperty("missing").EnumerateArray().Select(item => item.GetString()!).ToList();
        var duplicate = key.GetProperty("duplicate").EnumerateArray().Select(item => item.GetString()!).ToList();
        var changed = key.GetProperty("amountMismatch").EnumerateArray().ToList();
        var unknown = key.GetProperty("unknown").EnumerateArray().Select(item => item.GetProperty("providerReference").GetString()!).ToList();
        (missing.Count, duplicate.Count, changed.Count, unknown.Count).ShouldBe((2, 1, 2, 1));

        // Compare the file with the truth, the way reconciliation (M12) will.
        var truth = cleanBiller.ToDictionary(line => line.Reference, line => line.Amount);
        var counts = file.GroupBy(line => line.Reference).ToDictionary(group => group.Key, group => group.ToList());
        truth.Keys.Where(reference => !counts.ContainsKey(reference)).Order(StringComparer.Ordinal).ShouldBe(missing);
        counts.Where(pair => pair.Value.Count > 1).Select(pair => pair.Key).Order(StringComparer.Ordinal).ShouldBe(duplicate);
        counts.Keys.Where(reference => !truth.ContainsKey(reference)).Order(StringComparer.Ordinal).ShouldBe(unknown);
        counts.Where(pair => truth.TryGetValue(pair.Key, out var amount) && pair.Value[0].Amount != amount)
            .Select(pair => pair.Key).Order(StringComparer.Ordinal)
            .ShouldBe(changed.Select(item => item.GetProperty("providerReference").GetString()!));
        foreach (var change in changed)
        {
            var reference = change.GetProperty("providerReference").GetString()!;
            change.GetProperty("actualPaisa").GetInt64().ShouldBe(truth[reference]);
            change.GetProperty("reportedPaisa").GetInt64().ShouldBe(counts[reference][0].Amount);
        }

        using (var cleared = await admin.DeleteAsync(new Uri($"/admin/settlement/billers/PTCL/{date}/mismatches", UriKind.Relative), Cancel))
        {
            cleared.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        Parse(await FileAsync(client, $"/settlement/billers/PTCL/{date}")).Lines.Count.ShouldBe(cleanBiller.Count);
    }

    [Fact]
    public async Task Banks_report_the_raast_transfers_they_settled()
    {
        using var client = factory.Client();
        var transferId = NewId();
        (await client.PostJsonAsync("/raast/v1/transfers", new { transferId, amountPaisa = 123_400, toIban = TestAccounts.Iban("MUCB") })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await EventuallyAsync(
            async () => (await client.GetJsonAsync($"/raast/v1/transfers/{transferId}")).GetProperty("status").GetString() == "Completed",
            "The transfer did not settle.");

        var mcb = Parse(await FileAsync(client, $"/settlement/banks/MCB/{TodayInPakistan()}")).Lines;
        var hbl = Parse(await FileAsync(client, $"/settlement/banks/HBL/{TodayInPakistan()}")).Lines;

        mcb.ShouldContain(line => line.Client == transferId && line.Amount == 123_400);
        hbl.ShouldNotContain(line => line.Client == transferId);
    }

    [Fact]
    public async Task Unknown_files_are_404()
    {
        using var client = factory.Client();
        (await client.GetJsonAsync($"/settlement/banks/SCB/{TodayInPakistan()}", HttpStatusCode.NotFound)).GetProperty("code").GetString().ShouldBe("settlement.not_found");
        await client.GetJsonAsync("/settlement/billers/KE/15-11-2026", HttpStatusCode.NotFound);
    }
}
