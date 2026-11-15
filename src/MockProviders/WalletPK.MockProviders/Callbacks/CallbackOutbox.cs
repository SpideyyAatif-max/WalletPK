using System.Net.Http.Headers;
using System.Text;
using Dapper;
using Npgsql;
using WalletPK.MockProviders.Storage;
using WalletPK.Web.Security;

namespace WalletPK.MockProviders.Callbacks;

/// <summary>A callback still being delivered or given up on (for GET /admin/callbacks).</summary>
public sealed class CallbackRow
{
    public long Id { get; init; }

    public string Provider { get; init; } = "";

    public string Url { get; init; } = "";

    public string Payload { get; init; } = "";

    public int Attempts { get; init; }

    public DateTime NextAttemptAt { get; init; }

    public DateTime? DeliveredAt { get; init; }

    public DateTime? FailedAt { get; init; }

    public string? LastError { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Callbacks are written in the same transaction as the provider's state change (an outbox), then a
/// background loop delivers them, signed, retrying with back-off. A provider that settled a payment
/// therefore always tells WalletPK eventually, even across restarts.
/// </summary>
public sealed class CallbackOutbox(MockDatabase database)
{
    private const string Columns = """
        id AS Id, provider AS Provider, url AS Url, payload AS Payload, attempts AS Attempts,
        next_attempt_at AS NextAttemptAt, delivered_at AS DeliveredAt, failed_at AS FailedAt,
        last_error AS LastError, created_at AS CreatedAt
        """;

    public static Task EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string provider, string url, string payload, DateTimeOffset now) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO mock.callbacks (provider, url, payload, next_attempt_at, created_at)
            VALUES (@Provider, @Url, @Payload, @Now, @Now)
            """,
            new { Provider = provider, Url = url, Payload = payload, Now = now.UtcDateTime },
            transaction));

    public async Task<IReadOnlyList<CallbackRow>> RecentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<CallbackRow>(new CommandDefinition(
            $"SELECT {Columns} FROM mock.callbacks ORDER BY id DESC LIMIT @Limit",
            new { Limit = limit },
            cancellationToken: cancellationToken))];
    }

    /// <summary>Delivers the callbacks that are due; returns how many were attempted.</summary>
    public async Task<int> DeliverDueAsync(
        Func<CallbackRow, CancellationToken, Task<string?>> deliver,
        DateTimeOffset now,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var due = (await connection.QueryAsync<CallbackRow>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM mock.callbacks
            WHERE delivered_at IS NULL AND failed_at IS NULL AND next_attempt_at <= @Now
            ORDER BY id
            LIMIT 20
            FOR UPDATE SKIP LOCKED
            """,
            new { Now = now.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken))).ToList();

        foreach (var callback in due)
        {
            var error = await deliver(callback, cancellationToken);
            var attempts = callback.Attempts + 1;
            var backoff = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempts - 1)));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE mock.callbacks SET
                    attempts = @Attempts,
                    delivered_at = CASE WHEN @Error IS NULL THEN @Now END,
                    failed_at = CASE WHEN @Error IS NOT NULL AND @Attempts >= @MaxAttempts THEN @Now END,
                    next_attempt_at = @Next,
                    last_error = @Error
                WHERE id = @Id
                """,
                new { callback.Id, Attempts = attempts, Error = error, Now = now.UtcDateTime, Next = (now + backoff).UtcDateTime, MaxAttempts = maxAttempts },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return due.Count;
    }
}

/// <summary>Posts one callback, signed with the provider's webhook secret (WebhookSignature, M1.4).</summary>
public sealed class CallbackSender(IHttpClientFactory httpClients, MockProvidersOptions options, TimeProvider timeProvider)
{
    public const string HttpClientName = "callbacks";

    /// <summary>Null when delivered (2xx); otherwise why not.</summary>
    public async Task<string?> SendAsync(CallbackRow callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var secret = callback.Provider == Faults.Providers.Raast ? options.Raast.CallbackSecret : "";
        var body = Encoding.UTF8.GetBytes(callback.Payload);
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, callback.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (secret.Length > 0)
        {
            request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Compute(secret, timestamp, body));
        }

        request.Headers.Add("X-Mock-Callback-Id", callback.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Mock-Delivery-Attempt", (callback.Attempts + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (HttpRequestException exception)
        {
            return exception.Message;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "Timed out";
        }
    }
}

/// <summary>Delivers due callbacks in a loop.</summary>
internal sealed partial class CallbackDispatcher(
    CallbackOutbox outbox,
    CallbackSender sender,
    MockProvidersOptions options,
    TimeProvider timeProvider,
    ILogger<CallbackDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var attempted = await outbox.DeliverDueAsync(sender.SendAsync, timeProvider.GetUtcNow(), options.Callbacks.MaxAttempts, stoppingToken);
                if (attempted > 0)
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            await Task.Delay(options.Callbacks.PollMilliseconds, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Callback delivery loop failed; retrying")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
