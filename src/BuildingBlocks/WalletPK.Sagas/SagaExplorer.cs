using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace WalletPK.Sagas;

/// <summary>What to look for in the Saga explorer. Every filter is optional; newest activity first.</summary>
/// <param name="Type">A definition name, e.g. "bill-payment".</param>
/// <param name="State">A state, e.g. "DeadLettered".</param>
/// <param name="Active">true: still running; false: finished.</param>
/// <param name="StuckForMinutes">Running and unchanged for at least this long.</param>
/// <param name="UserId">A user the Saga is about: any top-level field of its data ("userId", "senderUserId", "payerUserId", ...).</param>
/// <param name="CorrelationId">The request that started it.</param>
/// <param name="Before">Paging: only Sagas last changed before this (the previous page's last UpdatedAt).</param>
/// <param name="Limit">1-200 (default 50).</param>
public sealed record SagaSearch(
    string? Type = null,
    string? State = null,
    bool? Active = null,
    int? StuckForMinutes = null,
    Guid? UserId = null,
    string? CorrelationId = null,
    DateTimeOffset? Before = null,
    int? Limit = null);

/// <summary>One Saga in a search result.</summary>
public sealed record SagaSummary(
    Guid Id,
    string Type,
    string State,
    bool IsCompleted,
    int Steps,
    string? CorrelationId,
    DateTimeOffset? WakeAt,
    string? WakeReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>One step of a Saga's timeline, with the trace it happened in (open it in Jaeger).</summary>
public sealed record SagaTimelineStep(int Sequence, string State, string? PreviousState, string Trigger, string Detail, string? TraceId, DateTimeOffset At);

/// <summary>A Saga with its data (as stored) and full timeline.</summary>
public sealed record SagaDetail(SagaSummary Saga, JsonElement Data, IReadOnlyList<SagaTimelineStep> Timeline);

/// <summary>How many Sagas of a type are in a state.</summary>
public sealed record SagaStateCount(string Type, string State, bool IsCompleted, int Count, DateTimeOffset OldestUpdatedAt);

/// <summary>The Saga explorer (M14.B5): search a service's Sagas, open one with its timeline, see what is where.</summary>
public interface ISagaExplorer
{
    Task<IReadOnlyList<SagaSummary>> SearchAsync(SagaSearch search, CancellationToken cancellationToken);

    Task<SagaDetail?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Counts by type and state: all running Sagas, and finished ones from the last <paramref name="finishedWithin"/>.</summary>
    Task<IReadOnlyList<SagaStateCount>> CountsAsync(TimeSpan finishedWithin, CancellationToken cancellationToken);
}

public sealed class SagaExplorer<TDbContext>(TDbContext db, TimeProvider timeProvider) : ISagaExplorer
    where TDbContext : DbContext
{
    public const int MaxLimit = 200;

    public async Task<IReadOnlyList<SagaSummary>> SearchAsync(SagaSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var sagas = search.UserId is { } userId
            ? db.Set<SagaInstance>().FromSql($"SELECT * FROM saga_instances WHERE EXISTS (SELECT 1 FROM jsonb_each_text(data) WHERE value = {userId.ToString()})")
            : db.Set<SagaInstance>();

        if (!string.IsNullOrWhiteSpace(search.Type))
        {
            sagas = sagas.Where(saga => saga.SagaType == search.Type);
        }

        if (!string.IsNullOrWhiteSpace(search.State))
        {
            sagas = sagas.Where(saga => saga.State == search.State);
        }

        if (search.Active is { } active)
        {
            sagas = sagas.Where(saga => saga.IsCompleted != active);
        }

        if (search.StuckForMinutes is { } minutes)
        {
            var since = timeProvider.GetUtcNow().AddMinutes(-Math.Max(0, minutes));
            sagas = sagas.Where(saga => !saga.IsCompleted && saga.UpdatedAt <= since);
        }

        if (!string.IsNullOrWhiteSpace(search.CorrelationId))
        {
            sagas = sagas.Where(saga => saga.CorrelationId == search.CorrelationId);
        }

        if (search.Before is { } before)
        {
            sagas = sagas.Where(saga => saga.UpdatedAt < before);
        }

        var rows = await sagas.AsNoTracking()
            .OrderByDescending(saga => saga.UpdatedAt)
            .ThenBy(saga => saga.Id)
            .Take(Math.Clamp(search.Limit ?? 50, 1, MaxLimit))
            .ToListAsync(cancellationToken);
        return [.. rows.Select(Summary)];
    }

    public async Task<SagaDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Set<SagaInstance>().AsNoTracking().SingleOrDefaultAsync(saga => saga.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var steps = await db.Set<SagaStep>().AsNoTracking()
            .Where(step => step.SagaId == id)
            .OrderBy(step => step.Sequence)
            .ToListAsync(cancellationToken);
        using var data = JsonDocument.Parse(row.Data);
        return new SagaDetail(
            Summary(row),
            data.RootElement.Clone(),
            [.. steps.Select(step => new SagaTimelineStep(step.Sequence, step.State, step.PreviousState, step.Trigger, step.Detail, step.TraceId, step.At))]);
    }

    public async Task<IReadOnlyList<SagaStateCount>> CountsAsync(TimeSpan finishedWithin, CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow() - finishedWithin;
        var counts = await db.Set<SagaInstance>().AsNoTracking()
            .Where(saga => !saga.IsCompleted || saga.CompletedAt >= since)
            .GroupBy(saga => new { saga.SagaType, saga.State, saga.IsCompleted })
            .Select(group => new { group.Key.SagaType, group.Key.State, group.Key.IsCompleted, Count = group.Count(), Oldest = group.Min(saga => saga.UpdatedAt) })
            .ToListAsync(cancellationToken);
        return [.. counts
            .OrderBy(count => count.SagaType, StringComparer.Ordinal)
            .ThenBy(count => count.IsCompleted)
            .ThenBy(count => count.State, StringComparer.Ordinal)
            .Select(count => new SagaStateCount(count.SagaType, count.State, count.IsCompleted, count.Count, count.Oldest))];
    }

    private static SagaSummary Summary(SagaInstance row) =>
        new(row.Id, row.SagaType, row.State, row.IsCompleted, row.StepCount, row.CorrelationId, row.WakeAt, row.WakeReason, row.CreatedAt, row.UpdatedAt, row.CompletedAt);
}
