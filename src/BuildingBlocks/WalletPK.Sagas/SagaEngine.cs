using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Sagas;

/// <summary>What happened to a message given to a Saga.</summary>
public enum SagaHandleOutcome
{
    /// <summary>The Saga moved on (its changes are saved with the caller's transaction, or its own).</summary>
    Handled,

    /// <summary>No Saga has this ID.</summary>
    NotFound,

    /// <summary>The Saga is finished, or does not handle this message: nothing changed.</summary>
    Ignored,
}

/// <summary>Starts Sagas and gives them messages (the engine of the service's database).</summary>
public interface ISagaEngine
{
    /// <summary>Starts a Saga with ID <paramref name="id"/>; false if it already exists.</summary>
    Task<bool> StartAsync<TData>(SagaDefinition<TData> definition, Guid id, TData data, string? correlationId, CancellationToken cancellationToken)
        where TData : class;

    /// <summary>Gives Saga <paramref name="sagaId"/> a message (a reply, a callback).</summary>
    Task<SagaHandleOutcome> HandleAsync(Guid sagaId, object message, string? causationId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction (or the caller's), so several Sagas can be
    /// started or moved on together: all of them, or none.
    /// </summary>
    Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken);
}

/// <summary>A Saga claimed for a timer step: the row as it was when taken, and its version then.</summary>
internal sealed record ClaimedSaga(SagaInstance Row, DateTimeOffset DueAt);

/// <summary>
/// Runs Sagas for one service's database (M6.3). Every change to a Saga, in one transaction:
/// the row's new state, data and timer, its timeline steps, and the messages it sends (outbox rows).
/// <list type="bullet">
/// <item><see cref="StartAsync{TData}"/> inserts a Saga; starting the same ID again does nothing (so a
/// retried request does not start a second transfer).</item>
/// <item><see cref="HandleAsync"/> gives a Saga a message. The row is locked for the step, so two
/// messages for one Saga never interleave. Inside an inbox transaction it joins it: the inbox row, the
/// Saga's changes and its outgoing messages commit together.</item>
/// <item>Timers: <see cref="ClaimDueAsync"/> takes due Sagas (with a lease), <see cref="RunTimerStepAsync"/>
/// runs one step outside any transaction and then saves its outcome, if the Saga has not changed meanwhile.</item>
/// </list>
/// </summary>
public sealed partial class SagaEngine<TDbContext>(
    TDbContext db,
    SagaRegistry registry,
    JsonEventSerializer serializer,
    TimeProvider timeProvider,
    SagaTimerOptions timerOptions,
    ILogger<SagaEngine<TDbContext>> logger) : ISagaEngine
    where TDbContext : DbContext
{
    /// <summary>Starts a Saga with ID <paramref name="id"/>; false if it already exists.</summary>
    /// <param name="definition">Its definition (its OnStart takes the first step).</param>
    /// <param name="id">Its ID: derive it from the request (an idempotency key), so retries find it.</param>
    /// <param name="data">Its first data.</param>
    /// <param name="correlationId">Carried by every message it sends.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    public async Task<bool> StartAsync<TData>(SagaDefinition<TData> definition, Guid id, TData data, string? correlationId, CancellationToken cancellationToken)
        where TData : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(data);

        var now = timeProvider.GetUtcNow();
        var changes = definition.Begin(id, data, now);
        var row = new SagaInstance
        {
            Id = id,
            SagaType = definition.Name,
            State = SagaStates.New,
            CorrelationId = correlationId,
            CreatedAt = now,
        };
        Apply(row, changes, trigger: "start", causationId: null, now, timerStep: false, insert: true);

        return await InTransactionAsync(
            async () =>
            {
                // Insert first, with ON CONFLICT: a second start of the same ID (a retried request, or two
                // at once) inserts nothing and leaves the first Saga alone.
                var inserted = await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO saga_instances (id, saga_type, state, data, is_completed, wake_at, wake_reason, version, step_count, correlation_id, created_at, updated_at, completed_at)
                    VALUES ({row.Id}, {row.SagaType}, {row.State}, CAST({row.Data} AS jsonb), {row.IsCompleted}, {row.WakeAt}, {row.WakeReason}, {row.Version}, {row.StepCount}, {row.CorrelationId}, {row.CreatedAt}, {row.UpdatedAt}, {row.CompletedAt})
                    ON CONFLICT (id) DO NOTHING
                    """,
                    cancellationToken);

                if (inserted == 0)
                {
                    DiscardPending();
                    return false;
                }

                await db.SaveChangesAsync(cancellationToken);
                SagaTelemetry.Started.Add(1, new KeyValuePair<string, object?>("saga_type", row.SagaType));
                return true;
            },
            cancellationToken);
    }

    /// <summary>Gives Saga <paramref name="sagaId"/> a message (a reply, a callback).</summary>
    /// <param name="sagaId">The Saga's ID.</param>
    /// <param name="message">The message; the definition's handler for its type decides what happens.</param>
    /// <param name="causationId">What caused it (the consumed event's ID); stored on outgoing messages.</param>
    /// <param name="cancellationToken">Cancels the step.</param>
    public async Task<SagaHandleOutcome> HandleAsync(Guid sagaId, object message, string? causationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return await InTransactionAsync(
            async () =>
            {
                var row = await LockAsync(sagaId, cancellationToken);
                if (row is null)
                {
                    return SagaHandleOutcome.NotFound;
                }

                if (row.IsCompleted)
                {
                    LogIgnoredCompleted(logger, row.SagaType, sagaId, message.GetType().Name, row.State);
                    return SagaHandleOutcome.Ignored;
                }

                var now = timeProvider.GetUtcNow();
                var changes = registry.Get(row.SagaType).Handle(row, message, now);
                if (changes is null)
                {
                    LogIgnoredUnhandled(logger, row.SagaType, sagaId, message.GetType().Name, row.State);
                    return SagaHandleOutcome.Ignored;
                }

                Apply(row, changes, message.GetType().Name, causationId, now, timerStep: false, insert: false);
                await db.SaveChangesAsync(cancellationToken);
                return SagaHandleOutcome.Handled;
            },
            cancellationToken);
    }

    /// <summary>
    /// Takes up to <paramref name="batchSize"/> due Sagas. Their wake time moves to the end of a lease, so
    /// no other timer loop takes them meanwhile; the rows are not locked while their steps run.
    /// </summary>
    internal async Task<IReadOnlyList<ClaimedSaga>> ClaimDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var leaseUntil = now.AddSeconds(timerOptions.LeaseSeconds);

        // One statement: pick the due rows nobody else is picking (SKIP LOCKED), move their wake time to
        // the lease end, and return them as they now are. "due" comes from the subquery, before the update.
        var rows = await db.Set<SagaInstance>()
            .FromSql($"""
                WITH due AS (
                    SELECT id, wake_at
                      FROM saga_instances
                     WHERE wake_at <= {now}
                       AND NOT is_completed
                     ORDER BY wake_at
                     LIMIT {batchSize}
                       FOR UPDATE SKIP LOCKED)
                UPDATE saga_instances AS saga
                   SET wake_at = {leaseUntil}
                  FROM due
                 WHERE saga.id = due.id
                RETURNING saga.id, saga.saga_type, saga.state, saga.data, saga.is_completed, due.wake_at, saga.wake_reason,
                          saga.version, saga.step_count, saga.correlation_id, saga.created_at, saga.updated_at, saga.completed_at
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // wake_at in the result is the time it was due (from "due"), not the lease end.
        return [.. rows.Select(row => new ClaimedSaga(row, row.WakeAt ?? now))];
    }

    /// <summary>
    /// Runs one timer step of a claimed Saga. The step itself (which may call outside systems) runs
    /// outside any transaction; its outcome is then saved only if the Saga's version is unchanged. If a
    /// message moved the Saga on meanwhile, the outcome is thrown away (the step's calls are idempotent,
    /// and the Saga will decide again from its new state).
    /// </summary>
    internal async Task RunTimerStepAsync(ClaimedSaga claimed, IServiceProvider services, CancellationToken cancellationToken)
    {
        var row = claimed.Row;
        var tags = new TagList { { "saga_type", row.SagaType }, { "reason", row.WakeReason } };
        var started = timeProvider.GetUtcNow();
        SagaTelemetry.TimerLag.Record(Math.Max(0, (started - claimed.DueAt).TotalSeconds), tags);

        using var activity = SagaTelemetry.Source.StartActivity($"saga {row.SagaType} timer {row.WakeReason}");
        activity?.SetTag("walletpk.saga.id", row.Id);
        activity?.SetTag("walletpk.saga.state", row.State);
        var stopwatch = Stopwatch.StartNew();

        ISagaChanges changes;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timerOptions.StepTimeoutSeconds));
            changes = await registry.Get(row.SagaType).WakeAsync(row, services, started, timeout.Token);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            SagaTelemetry.TimerSteps.Add(1, [.. tags, new("outcome", "failed")]);
            LogTimerStepFailed(logger, exception, row.SagaType, row.Id, row.WakeReason, timerOptions.RetryDelaySeconds);
            await ScheduleRetryAsync(row, cancellationToken);
            return;
        }
        finally
        {
            SagaTelemetry.TimerStepDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        }

        var applied = await InTransactionAsync(
            async () =>
            {
                var current = await LockAsync(row.Id, cancellationToken);
                if (current is null || current.IsCompleted || current.Version != row.Version)
                {
                    return false;
                }

                Apply(current, changes, $"timer:{row.WakeReason}", causationId: null, timeProvider.GetUtcNow(), timerStep: true, insert: false);
                await db.SaveChangesAsync(cancellationToken);
                return true;
            },
            cancellationToken);

        SagaTelemetry.TimerSteps.Add(1, [.. tags, new("outcome", applied ? "applied" : "stale")]);
        if (!applied)
        {
            DiscardPending();
            LogTimerStepStale(logger, row.SagaType, row.Id, row.WakeReason);
        }
    }

    private async Task ScheduleRetryAsync(SagaInstance row, CancellationToken cancellationToken)
    {
        var retryAt = timeProvider.GetUtcNow().AddSeconds(timerOptions.RetryDelaySeconds);
        try
        {
            // Only if nothing changed meanwhile; otherwise the Saga's own (new) timer stands.
            await db.Database.ExecuteSqlAsync(
                $"UPDATE saga_instances SET wake_at = {retryAt} WHERE id = {row.Id} AND version = {row.Version} AND NOT is_completed",
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The lease still runs out and the step is retried then.
            LogRetryNotScheduled(logger, exception, row.Id);
        }
    }

    /// <summary>Reads the Saga and locks its row until the transaction ends.</summary>
    private async Task<SagaInstance?> LockAsync(Guid sagaId, CancellationToken cancellationToken)
    {
        // A copy this context already tracks would be returned as it was, not as the database has it now.
        foreach (var tracked in db.ChangeTracker.Entries<SagaInstance>().Where(entry => entry.Entity.Id == sagaId).ToList())
        {
            tracked.State = EntityState.Detached;
        }

        var rows = await db.Set<SagaInstance>()
            .FromSql($"SELECT * FROM saga_instances WHERE id = {sagaId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Writes a step's outcome to the (tracked or new) row, its timeline and the outbox.</summary>
    private void Apply(SagaInstance row, ISagaChanges changes, string trigger, string? causationId, DateTimeOffset now, bool timerStep, bool insert)
    {
        var previousState = row.State;
        row.State = changes.State;
        row.Data = changes.SerializedData;
        row.UpdatedAt = now;
        if (!insert)
        {
            row.Version++;
        }

        if (changes.Wake is { } wake)
        {
            row.WakeAt = wake.At;
            row.WakeReason = wake.Reason;
        }
        else if (timerStep)
        {
            // A timer fires once; the step set no new one.
            row.WakeAt = null;
            row.WakeReason = null;
        }

        if (changes.IsCompleted)
        {
            row.IsCompleted = true;
            row.CompletedAt = now;
            row.WakeAt = null;
            row.WakeReason = null;
            SagaTelemetry.Completed.Add(1, new("saga_type", row.SagaType), new("state", row.State));
        }

        var traceId = Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.TraceId.ToHexString() : null;
        foreach (var step in changes.Steps)
        {
            row.StepCount++;
            db.Set<SagaStep>().Add(new SagaStep
            {
                SagaId = row.Id,
                Sequence = row.StepCount,
                State = step.State,
                PreviousState = step.PreviousState,
                Trigger = Truncate(trigger, 100),
                Detail = Truncate(step.Detail, 1000),
                TraceId = traceId,
                At = now,
            });
        }

        if (row.State != previousState)
        {
            SagaTelemetry.Transitions.Add(1, new("saga_type", row.SagaType), new("state", row.State));
        }

        foreach (var message in changes.Outgoing)
        {
            db.AddToOutbox(serializer, message, now, row.CorrelationId, causationId);
        }
    }

    /// <summary>Joins the caller's transaction (the inbox's) if there is one, otherwise runs in its own.</summary>
    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await work();
        }

        IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using (transaction)
        {
            try
            {
                var result = await work();
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                DiscardPending();
                throw;
            }
        }
    }

    /// <summary>Forgets steps and outbox rows added for a step that is not saved.</summary>
    private void DiscardPending()
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added && entry.Entity is SagaStep or OutboxMessage).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    [LoggerMessage(Level = LogLevel.Information, Message = "{SagaType} {SagaId} is finished ({State}); ignored {Message}")]
    private static partial void LogIgnoredCompleted(ILogger logger, string sagaType, Guid sagaId, string message, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{SagaType} {SagaId} does not handle {Message} (state {State}); ignored it")]
    private static partial void LogIgnoredUnhandled(ILogger logger, string sagaType, Guid sagaId, string message, string state);

    [LoggerMessage(Level = LogLevel.Error, Message = "Timer step {WakeReason} of {SagaType} {SagaId} failed; retrying in {RetrySeconds}s")]
    private static partial void LogTimerStepFailed(ILogger logger, Exception exception, string sagaType, Guid sagaId, string? wakeReason, int retrySeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Timer step {WakeReason} of {SagaType} {SagaId} was overtaken by a newer change; its outcome was discarded")]
    private static partial void LogTimerStepStale(ILogger logger, string sagaType, Guid sagaId, string? wakeReason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not schedule the retry of Saga {SagaId}; it runs again when its lease ends")]
    private static partial void LogRetryNotScheduled(ILogger logger, Exception exception, Guid sagaId);
}

/// <summary>Runs due timers once (the background loop calls it; tests call it directly).</summary>
public sealed class SagaTimerRunner<TDbContext>(IServiceScopeFactory scopes, SagaTimerOptions options)
    where TDbContext : DbContext
{
    /// <summary>Claims one batch of due Sagas and runs their steps; returns how many were due.</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ClaimedSaga> claimed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            claimed = await scope.ServiceProvider.GetRequiredService<SagaEngine<TDbContext>>().ClaimDueAsync(options.BatchSize, cancellationToken);
        }

        await Parallel.ForEachAsync(
            claimed,
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = cancellationToken },
            async (saga, token) =>
            {
                // Each step gets its own scope: its own DbContext and whatever clients the step resolves.
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SagaEngine<TDbContext>>().RunTimerStepAsync(saga, scope.ServiceProvider, token);
            });

        return claimed.Count;
    }
}
