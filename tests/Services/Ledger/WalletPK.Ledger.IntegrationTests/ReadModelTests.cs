using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WalletPK.Common.Identifiers;
using WalletPK.Contracts.Ledger;
using WalletPK.Ledger.Application.ReadModels;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Infrastructure.ReadModels;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using WalletPK.Messaging;
using WalletPK.Messaging.Serialization;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>
/// M4.4-M4.6: the read models are fed by the real projection from the real Kafka topic, queried over
/// HTTP, survive duplicate deliveries, answer fast on a big history, and rebuild identically from offset 0.
/// </summary>
[Collection(LedgerCollection.Name)]
public class ReadModelTests(LedgerApiFactory factory, ITestOutputHelper output)
{
    private async Task<NpgsqlConnection> OpenAsync()
    {
        _ = factory.Services; // Starts the service, which migrates the database.
        var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Cancel);
        return connection;
    }

    private Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action) => factory.InScopeAsync(action);

    private async Task<JsonElement> GetAsync(HttpClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(expected);
        var body = await response.Content.ReadAsStringAsync(Cancel);
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private HttpClient Staff(string role) => factory.CreateClientFor(UserId.New(), phoneNumber: null, role);

    [Fact]
    public async Task Balance_and_history_follow_the_ledger_with_the_wallets_sequence()
    {
        var sender = await factory.CustomerAsync(rupees: 2_000);
        var recipient = await factory.CustomerAsync();
        using (var sent = await sender.SendAsync(recipient.Phone, 50_000, NewKey()))
        {
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await sender.WaitForHistoryAsync(entries: 2);
        var ledger = await factory.AccountAsync(sender.WalletId);

        var balance = await GetAsync(sender.Client, "/api/wallets/me/balance");
        balance.GetProperty("walletId").GetString().ShouldBe(sender.WalletId.ToString());
        balance.GetProperty("walletNumber").GetString().ShouldBe(sender.Phone);
        Paisa(balance, "balance").ShouldBe(ledger.Balance);
        balance.GetProperty("sequence").GetInt64().ShouldBe(ledger.EntrySequence);

        var history = (await sender.HistoryAsync()).GetProperty("items").EnumerateArray().ToList();
        history.Select(item => item.GetProperty("sequence").GetInt64()).ShouldBe([2, 1]);
        Paisa(history[0], "balanceAfter").ShouldBe(ledger.Balance);
    }

    [Fact]
    public async Task A_new_wallet_is_in_the_read_model_before_its_first_entry()
    {
        var customer = await factory.CustomerAsync();

        await KafkaTestKit.EventuallyAsync(async () =>
        {
            await using var connection = await OpenAsync();
            return await connection.ExecuteScalarAsync<long?>(
                "SELECT last_sequence FROM read.wallet_balances WHERE wallet_id = @Id AND opened_at IS NOT NULL",
                new { Id = customer.WalletId.Value }) == 0;
        }, because: "ledger.wallet_opened never reached the balance read model.");

        var balance = await GetAsync(customer.Client, "/api/wallets/me/balance");
        Paisa(balance, "balance").ShouldBe(0);
        balance.GetProperty("sequence").GetInt64().ShouldBe(0);
        (await customer.HistoryAsync()).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task The_statement_filters_by_type_direction_and_date()
    {
        var customer = await factory.CustomerAsync(rupees: 1_000);
        var other = await factory.CustomerAsync(rupees: 1_000);
        using (var sent = await customer.SendAsync(other.Phone, 10_000, NewKey()))
        {
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var received = await other.SendAsync(customer.Phone, 20_000, NewKey()))
        {
            received.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await customer.WaitForHistoryAsync(entries: 3);

        static List<long> Amounts(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(item => Paisa(item, "amount"))];

        Amounts(await customer.HistoryAsync("?type=CashIn")).ShouldBe([100_000]);
        Amounts(await customer.HistoryAsync("?type=p2ptransfer")).ShouldBe([20_000, 10_000]);
        Amounts(await customer.HistoryAsync("?direction=in")).ShouldBe([20_000, 100_000]);
        Amounts(await customer.HistoryAsync("?direction=OUT&type=P2PTransfer")).ShouldBe([10_000]);

        // The test clock stands still, so every entry has the same time: "to" is exclusive, "from" inclusive.
        var postedAt = (await customer.HistoryAsync()).GetProperty("items")[0].GetProperty("postedAt").GetDateTimeOffset();
        string At(TimeSpan offset) => Uri.EscapeDataString((postedAt + offset).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        Amounts(await customer.HistoryAsync($"?from={At(TimeSpan.Zero)}&to={At(TimeSpan.FromSeconds(1))}")).Count.ShouldBe(3);
        Amounts(await customer.HistoryAsync($"?to={At(TimeSpan.Zero)}")).ShouldBeEmpty();
        Amounts(await customer.HistoryAsync($"?from={At(TimeSpan.FromSeconds(1))}")).ShouldBeEmpty();

        foreach (var invalid in new[] { "?type=Bribe", "?type=2", "?direction=sideways", $"?from={At(TimeSpan.Zero)}&to={At(TimeSpan.Zero)}" })
        {
            var problem = await GetAsync(customer.Client, $"/api/wallets/me/transactions{invalid}", HttpStatusCode.BadRequest);
            problem.GetProperty("code").GetString().ShouldBe("wallet.statement_filter_invalid", invalid);
        }
    }

    [Fact]
    public async Task Per_minute_metrics_count_each_journal_once_and_are_for_staff_only()
    {
        var minute = factory.Clock.GetUtcNow();
        minute = new DateTimeOffset(minute.Ticks - (minute.Ticks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
        var range = $"from={Uri.EscapeDataString(minute.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(minute.AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture))}&type=P2PTransfer";
        using var ops = Staff("ops");

        async Task<(long Count, long Volume)> P2PAsync()
        {
            var minutes = (await GetAsync(ops, $"/api/ledger/metrics?{range}")).GetProperty("minutes").EnumerateArray().ToList();
            return minutes.Count == 0 ? (0, 0) : (minutes.Single().GetProperty("count").GetInt64(), Paisa(minutes.Single(), "volume"));
        }

        var sender = await factory.CustomerAsync(rupees: 5_000);
        var recipient = await factory.CustomerAsync();
        await sender.WaitForHistoryAsync(entries: 1);
        await KafkaTestKit.EventuallyAsync(async () => await StatusAsync() is { } status && status.ProjectedJournals > 0);
        var (count, volume) = await P2PAsync();

        foreach (var paisa in new long[] { 150_000, 50_000 })
        {
            using var sent = await sender.SendAsync(recipient.Phone, paisa, NewKey());
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Journal volume is the total debited: amount + fee + tax (Rs. 1,500 costs Rs. 7.50 + 16% tax; Rs. 500 is free).
        await KafkaTestKit.EventuallyAsync(async () => await P2PAsync() == (count + 2, volume + 150_870 + 50_000),
            because: "The P2P metrics for this minute did not grow by exactly the two transfers.");

        using var customer = factory.CreateClientFor(UserId.New(), NewPhone());
        await GetAsync(customer, $"/api/ledger/metrics?{range}", HttpStatusCode.Forbidden);
        (await GetAsync(ops, "/api/ledger/metrics?from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z", HttpStatusCode.BadRequest))
            .GetProperty("code").GetString().ShouldBe("ledger.metrics_range_invalid");
        (await GetAsync(ops, "/api/ledger/metrics?from=2026-01-01T00:00:00Z&to=2026-01-03T00:00:00Z", HttpStatusCode.BadRequest))
            .GetProperty("code").GetString().ShouldBe("ledger.metrics_range_invalid");
    }

    [Fact]
    public async Task Delivering_the_same_events_again_changes_nothing_and_an_old_entry_never_rolls_a_balance_back()
    {
        var sender = await factory.CustomerAsync(rupees: 1_000);
        var recipient = await factory.CustomerAsync();
        var key = NewKey();
        using (var sent = await sender.SendAsync(recipient.Phone, 30_000, key))
        {
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var transactionId = WalletService.TransactionIdFor(sender.Id, key).Value;
        bool Mine(ReadMessage message) =>
            message.Key == sender.WalletId.ToString()
            || (message.Payload.TryGetProperty("transactionId", out var id) && id.GetGuid() == transactionId);

        // wallet_opened + cash-in + transfer for the sender's wallet, plus the transfer's journal_posted.
        var events = factory.ReadUntil(KafkaTopics.LedgerEvents, read => read.Count(Mine) >= 5).Where(Mine).ToList();
        await sender.WaitForHistoryAsync(entries: 2);
        await KafkaTestKit.EventuallyAsync(async () => await ProjectedAsync(transactionId));
        var before = await SettledSnapshotAsync();

        var generation = (await StatusAsync()).Generation;
        await InScopeAsync(async services =>
        {
            var projector = services.GetRequiredService<LedgerProjector>();
            var group = services.GetRequiredService<ReadModelOptions>().ConsumerGroupFor(generation);

            // Everything again, then newest first: duplicates are ignored and the cash-in (sequence 1)
            // arriving after the transfer (sequence 2) does not put the old balance back.
            foreach (var message in events.Concat(Enumerable.Reverse(events)))
            {
                await projector.ProcessAsync(message.Envelope, group, Cancel);
            }

            return 0;
        });

        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_projector_of_an_old_generation_writes_nothing()
    {
        var walletId = Guid.CreateVersion7();
        var entry = new WalletEntryPosted(
            walletId, Guid.CreateVersion7(), "+923009999999", "CustomerWallet", 1, Guid.CreateVersion7(), "CashIn", "test",
            "Credit", 100, 100, null, DateTimeOffset.UtcNow);

        try
        {
            await InScopeAsync(async services =>
            {
                var envelope = services.GetRequiredService<JsonEventSerializer>().Wrap(entry);
                var projector = services.GetRequiredService<LedgerProjector>();
                var options = services.GetRequiredService<ReadModelOptions>();
                var generation = (await StatusAsync()).Generation;

                await projector.ProcessAsync(envelope, options.ConsumerGroupFor(generation - 1), Cancel);
                (await HistoryRowsAsync(walletId)).ShouldBe(0);

                await projector.ProcessAsync(envelope, options.ConsumerGroupFor(generation), Cancel);
                (await HistoryRowsAsync(walletId)).ShouldBe(1);
                return 0;
            });
        }
        finally
        {
            await using var connection = await OpenAsync();
            await connection.ExecuteAsync(
                "DELETE FROM read.transaction_history WHERE wallet_id = @Id; DELETE FROM read.wallet_balances WHERE wallet_id = @Id;",
                new { Id = walletId });
        }
    }

    [Fact]
    public async Task History_queries_stay_under_10_ms_at_p95_on_a_10k_entry_wallet()
    {
        var ownerId = UserId.New();
        var walletId = Guid.CreateVersion7();
        await using (var connection = await OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO read.wallet_balances (wallet_id, owner_id, wallet_number, wallet_type, balance_paisa, last_sequence, opened_at, updated_at)
                VALUES (@WalletId, @OwnerId, '+923001112222', 'CustomerWallet', 1000000, 10000, now(), now());

                INSERT INTO read.transaction_history
                SELECT @WalletId, g, gen_random_uuid(),
                       CASE WHEN g % 10 = 0 THEN 'CashIn' ELSE 'P2PTransfer' END, 'seeded',
                       CASE WHEN g % 2 = 0 THEN 'Credit' ELSE 'Debit' END,
                       100, g * 100, '+923003334444', now() - (10000 - g) * interval '1 minute'
                FROM generate_series(1, 10000) AS g;

                ANALYZE read.transaction_history;
                ANALYZE read.wallet_balances;
                """,
                new { WalletId = walletId, OwnerId = ownerId.Value });
        }

        try
        {
            using var client = factory.CreateClientFor(ownerId, "+923001112222");
            var queries = new[]
            {
                "?limit=20",
                "?limit=20&before=5000",
                "?limit=20&before=150",
                "?limit=50&type=CashIn",
                "?limit=20&direction=in&before=9000",
                $"?limit=20&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-3).ToString("O", System.Globalization.CultureInfo.InvariantCulture))}",
            };

            foreach (var query in queries)
            {
                var page = await GetAsync(client, $"/api/wallets/me/transactions{query}");
                page.GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0, query);
            }

            // Through the whole HTTP stack (auth, the wallet lookup, the history query, JSON), in process.
            var timings = new List<double>();
            for (var round = 0; round < 50; round++)
            {
                foreach (var query in queries)
                {
                    var stopwatch = Stopwatch.StartNew();
                    using var response = await client.GetAsync(new Uri($"/api/wallets/me/transactions{query}", UriKind.Relative), Cancel);
                    await response.Content.ReadAsByteArrayAsync(Cancel);
                    stopwatch.Stop();
                    response.StatusCode.ShouldBe(HttpStatusCode.OK);
                    timings.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
            }

            timings.Sort();
            var p50 = timings[timings.Count / 2];
            var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
            output.WriteLine($"{timings.Count} statement requests on a 10,000-entry wallet: p50 {p50:F2} ms, p95 {p95:F2} ms, max {timings[^1]:F2} ms");
            p95.ShouldBeLessThan(10, $"p50 {p50:F2} ms");

            // Deep pages use the primary key, not a scan.
            await using var connection = await OpenAsync();
            var plan = string.Join("\n", await connection.QueryAsync<string>(
                "EXPLAIN SELECT * FROM read.transaction_history WHERE wallet_id = @WalletId AND sequence < 150 ORDER BY sequence DESC LIMIT 21",
                new { WalletId = walletId }));
            plan.ShouldContain("Index Scan Backward using transaction_history_pkey");
        }
        finally
        {
            await using var connection = await OpenAsync();
            await connection.ExecuteAsync(
                "DELETE FROM read.transaction_history WHERE wallet_id = @Id; DELETE FROM read.wallet_balances WHERE wallet_id = @Id;",
                new { Id = walletId });
        }
    }

    [Fact]
    public async Task A_rebuild_replays_ledger_events_from_offset_0_into_identical_read_models()
    {
        // Some traffic of our own, so the replay has work to do.
        var sender = await factory.CustomerAsync(rupees: 3_000);
        var recipient = await factory.CustomerAsync();
        for (var i = 0; i < 3; i++)
        {
            using var sent = await sender.SendAsync(recipient.Phone, 10_000 + i, NewKey());
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await sender.WaitForHistoryAsync(entries: 4);
        var before = await SettledSnapshotAsync();
        var status = await StatusAsync();

        using var ops = Staff("ops");
        using (var refused = await ops.PostAsync(new Uri("/api/ledger/read-models/rebuild", UriKind.Relative), null, Cancel))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var admin = Staff("admin");
        using (var accepted = await admin.PostAsync(new Uri("/api/ledger/read-models/rebuild", UriKind.Relative), null, Cancel))
        {
            accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await JsonAsync(accepted)).GetProperty("generation").GetInt64().ShouldBe(status.Generation + 1);
        }

        await KafkaTestKit.EventuallyAsync(
            async () => await SnapshotAsync() == before,
            TimeSpan.FromSeconds(60),
            "The rebuilt read models never matched the ones before the rebuild.");

        var after = await GetAsync(ops, "/api/ledger/read-models");
        after.GetProperty("generation").GetInt64().ShouldBe(status.Generation + 1);
        after.GetProperty("historyRows").GetInt64().ShouldBe(status.HistoryRows);
        after.GetProperty("rebuiltAt").ValueKind.ShouldBe(JsonValueKind.String);

        // And the new generation keeps projecting new traffic.
        using (var sent = await sender.SendAsync(recipient.Phone, 5_000, NewKey()))
        {
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await sender.WaitForHistoryAsync(entries: 5);
    }

    private Task<ReadModelStatus> StatusAsync() =>
        InScopeAsync(services => services.GetRequiredService<IReadModelMaintenance>().GetStatusAsync(Cancel));

    private async Task<bool> ProjectedAsync(Guid transactionId)
    {
        await using var connection = await OpenAsync();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM read.projected_journals WHERE transaction_id = @Id)", new { Id = transactionId });
    }

    private async Task<long> HistoryRowsAsync(Guid walletId)
    {
        await using var connection = await OpenAsync();
        return await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM read.transaction_history WHERE wallet_id = @Id", new { Id = walletId });
    }

    /// <summary>Every row of every read-model table, as text, in a stable order.</summary>
    private async Task<string> SnapshotAsync()
    {
        await using var connection = await OpenAsync();
        var parts = new List<string>();
        foreach (var table in new[] { "wallet_balances", "transaction_history", "projected_journals", "metrics_per_minute" })
        {
            var rows = await connection.QueryAsync<string>($"SELECT t::text FROM read.{table} t ORDER BY 1");
            parts.Add($"{table}:\n{string.Join("\n", rows)}");
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>A snapshot once the projection has caught up with everything earlier tests did (two equal reads in a row).</summary>
    private async Task<string> SettledSnapshotAsync()
    {
        var previous = await SnapshotAsync();
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(500, Cancel);
            var current = await SnapshotAsync();
            if (current == previous)
            {
                return current;
            }

            previous = current;
        }

        throw new TimeoutException("The read models never settled.");
    }
}
