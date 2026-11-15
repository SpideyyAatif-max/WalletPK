using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace WalletPK.Sagas;

/// <summary>A Saga as stored, with its data read back and its timeline (oldest step first).</summary>
public sealed record SagaSnapshot<TData>(
    Guid Id,
    string SagaType,
    string State,
    bool IsCompleted,
    TData Data,
    DateTimeOffset? WakeAt,
    string? WakeReason,
    int Version,
    string? CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<SagaStep> Steps);

/// <summary>Reads Sagas (no locks; for status pages and queries).</summary>
public interface ISagaReader
{
    Task<SagaSnapshot<TData>?> FindAsync<TData>(Guid id, CancellationToken cancellationToken)
        where TData : class;
}

/// <summary>Reads Sagas from <typeparamref name="TDbContext"/>.</summary>
public sealed class SagaReader<TDbContext>(TDbContext db) : ISagaReader
    where TDbContext : DbContext
{
    public async Task<SagaSnapshot<TData>?> FindAsync<TData>(Guid id, CancellationToken cancellationToken)
        where TData : class
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
        return ToSnapshot<TData>(row, steps);
    }

    /// <summary>Reads data stored by a definition (for lists built from saga_instances rows).</summary>
    public static TData ReadData<TData>(SagaInstance row)
        where TData : class
    {
        ArgumentNullException.ThrowIfNull(row);
        return JsonSerializer.Deserialize<TData>(row.Data, SagaJson.Options)!;
    }

    public static SagaSnapshot<TData> ToSnapshot<TData>(SagaInstance row, IReadOnlyList<SagaStep> steps)
        where TData : class
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(row.Id, row.SagaType, row.State, row.IsCompleted, ReadData<TData>(row), row.WakeAt, row.WakeReason, row.Version,
            row.CorrelationId, row.CreatedAt, row.UpdatedAt, row.CompletedAt, steps);
    }
}
