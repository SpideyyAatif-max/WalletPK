using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace WalletPK.MockProviders.Storage;

public static class OperationStatus
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Rejected = "Rejected";
}

/// <summary>One thing a provider accepted (a row of mock.operations).</summary>
public sealed class Operation
{
    public string Provider { get; init; } = "";

    /// <summary>The caller's unique ID (transferId, paymentId, topupId).</summary>
    public string ClientId { get; init; } = "";

    public string Kind { get; init; } = "";

    /// <summary>The provider's own reference, as in its settlement file.</summary>
    public string Reference { get; init; } = "";

    /// <summary>SHA-256 of the request, so a reused ID with a different body is refused.</summary>
    public string RequestHash { get; init; } = "";

    public long AmountPaisa { get; init; }

    public string Counterparty { get; init; } = "";

    /// <summary>Whose settlement file lists it: "bank:HBL", "biller:KE", "telco:JAZZ".</summary>
    public string SettlementParty { get; init; } = "";

    public string Status { get; init; } = OperationStatus.Pending;

    public string? Reason { get; init; }

    public string Details { get; init; } = "{}";

    public DateTime CreatedAt { get; init; }

    public DateTime? CompletedAt { get; init; }

    public DateTime? ProcessAfter { get; init; }

    public T DetailsAs<T>() => JsonSerializer.Deserialize<T>(Details, JsonSerializerOptions.Web)!;

    public static string HashOf(object request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonSerializerOptions.Web))));
}

/// <summary>Reads and writes mock.operations.</summary>
public sealed class OperationStore(MockDatabase database)
{
    private const string Columns = """
        provider AS Provider, client_id AS ClientId, kind AS Kind, reference AS Reference,
        request_hash AS RequestHash, amount_paisa AS AmountPaisa, counterparty AS Counterparty,
        settlement_party AS SettlementParty, status AS Status, reason AS Reason, details::text AS Details,
        created_at AS CreatedAt, completed_at AS CompletedAt, process_after AS ProcessAfter
        """;

    /// <summary>
    /// Stores the operation unless one with the same (provider, client ID) exists; returns whichever is
    /// stored and whether it is new. A unique violation (a bill paid twice) surfaces as PostgresException.
    /// </summary>
    public async Task<(Operation Stored, bool Created)> AddOrGetAsync(Operation operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var connection = await database.OpenAsync(cancellationToken);
        var inserted = await connection.QuerySingleOrDefaultAsync<Operation>(new CommandDefinition(
            $"""
            INSERT INTO mock.operations
                (provider, client_id, kind, reference, request_hash, amount_paisa, counterparty, settlement_party,
                 status, reason, details, created_at, completed_at, process_after)
            VALUES (@Provider, @ClientId, @Kind, @Reference, @RequestHash, @AmountPaisa, @Counterparty, @SettlementParty,
                    @Status, @Reason, @Details::jsonb, @CreatedAt, @CompletedAt, @ProcessAfter)
            ON CONFLICT (provider, client_id) DO NOTHING
            RETURNING {Columns}
            """,
            operation,
            cancellationToken: cancellationToken));

        return inserted is not null
            ? (inserted, true)
            : ((await GetAsync(operation.Provider, operation.ClientId, cancellationToken))!, false);
    }

    public async Task<Operation?> GetAsync(string provider, string clientId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Operation>(new CommandDefinition(
            $"SELECT {Columns} FROM mock.operations WHERE provider = @Provider AND client_id = @ClientId",
            new { Provider = provider, ClientId = clientId },
            cancellationToken: cancellationToken));
    }

    /// <summary>The latest operations of a provider for one counterparty (messages sent to a number or address), newest first.</summary>
    public async Task<IReadOnlyList<Operation>> RecentToAsync(string provider, string counterparty, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<Operation>(new CommandDefinition(
            $"SELECT {Columns} FROM mock.operations WHERE provider = @Provider AND counterparty = @Counterparty ORDER BY created_at DESC LIMIT @Limit",
            new { Provider = provider, Counterparty = counterparty, Limit = limit },
            cancellationToken: cancellationToken));
        return [.. rows];
    }

    /// <summary>The completed bill payment for this consumer and month, if any.</summary>
    public async Task<Operation?> FindPaidBillAsync(string provider, string consumerNumber, string billingMonth, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Operation>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM mock.operations
            WHERE provider = @Provider AND counterparty = @Consumer AND kind = 'bill_payment'
              AND status = 'Completed' AND details->>'billingMonth' = @Month
            """,
            new { Provider = provider, Consumer = consumerNumber, Month = billingMonth },
            cancellationToken: cancellationToken));
    }

    /// <summary>Completed operations in a settlement party's file for [from, to).</summary>
    public async Task<IReadOnlyList<Operation>> CompletedForSettlementAsync(string party, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<Operation>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM mock.operations
            WHERE settlement_party = @Party AND status = 'Completed' AND completed_at >= @From AND completed_at < @To
            ORDER BY reference
            """,
            new { Party = party, From = from.UtcDateTime, To = to.UtcDateTime },
            cancellationToken: cancellationToken));
        return [.. rows];
    }

    /// <summary>
    /// Locks up to <paramref name="limit"/> pending operations of <paramref name="kind"/> that are due,
    /// runs <paramref name="settle"/> for each and stores the outcome, all in one transaction.
    /// Several instances can run this at once (FOR UPDATE SKIP LOCKED).
    /// </summary>
    public async Task<int> SettleDueAsync(
        string kind,
        DateTimeOffset now,
        int limit,
        Func<Operation, NpgsqlConnection, NpgsqlTransaction, Task<SettleOutcome?>> settle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settle);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var due = (await connection.QueryAsync<Operation>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM mock.operations
            WHERE status = 'Pending' AND kind = @Kind AND process_after <= @Now
            ORDER BY process_after
            LIMIT @Limit
            FOR UPDATE SKIP LOCKED
            """,
            new { Kind = kind, Now = now.UtcDateTime, Limit = limit },
            transaction,
            cancellationToken: cancellationToken))).ToList();

        foreach (var operation in due)
        {
            var outcome = await settle(operation, connection, transaction);
            await connection.ExecuteAsync(new CommandDefinition(
                outcome is null
                    ? "UPDATE mock.operations SET process_after = @Retry WHERE provider = @Provider AND client_id = @ClientId"
                    : """
                      UPDATE mock.operations SET status = @Status, reason = @Reason, completed_at = @Now, process_after = NULL
                      WHERE provider = @Provider AND client_id = @ClientId
                      """,
                new
                {
                    operation.Provider,
                    operation.ClientId,
                    outcome?.Status,
                    outcome?.Reason,
                    Now = now.UtcDateTime,
                    Retry = now.AddSeconds(2).UtcDateTime,
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return due.Count;
    }
}

/// <summary>How a pending operation ended.</summary>
public sealed record SettleOutcome(string Status, string? Reason);
