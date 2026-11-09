namespace WalletPK.Ledger.Infrastructure.Audit;

/// <summary>
/// One row of the tamper-evident audit log (M3.10). Written in the same transaction as the change it
/// describes, so a posting can never exist without its audit row. Shortly afterwards the
/// <see cref="AuditSealer"/> links it into the hash chain: it gets the next chain sequence, the previous
/// row's hash and its own SHA-256 hash. After that the database refuses any change to it.
/// </summary>
public sealed class AuditRecord
{
    private AuditRecord()
    {
    }

    /// <summary>Insert order (identity). Not the chain order: rows are chained in the order they are sealed.</summary>
    public long Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>For example "journal.posted", "hold.placed", "account.opened".</summary>
    public string EventType { get; private set; } = null!;

    /// <summary>"journal", "hold" or "account".</summary>
    public string SubjectType { get; private set; } = null!;

    public string SubjectId { get; private set; } = null!;

    /// <summary>"user:{id}" for a signed-in caller, otherwise "system".</summary>
    public string Actor { get; private set; } = null!;

    public string? CorrelationId { get; private set; }

    /// <summary>What happened, as JSON. Hashed byte for byte as stored.</summary>
    public string Payload { get; private set; } = null!;

    /// <summary>Position in the chain (1, 2, 3...), set when sealed.</summary>
    public long? ChainSequence { get; private set; }

    /// <summary>Hash of the row before this one in the chain (64 zeros for the first row).</summary>
    public string? PreviousHash { get; private set; }

    /// <summary>SHA-256 (hex) over this row's content and <see cref="PreviousHash"/>.</summary>
    public string? Hash { get; private set; }

    public DateTimeOffset? SealedAt { get; private set; }

    public bool IsSealed => Hash is not null;

    public static AuditRecord Create(
        DateTimeOffset occurredAt, string eventType, string subjectType, string subjectId, string actor, string? correlationId, string payload) =>
        new()
        {
            // PostgreSQL keeps microseconds; trimming here keeps the in-memory value identical to the stored one.
            OccurredAt = occurredAt.AddTicks(-(occurredAt.UtcTicks % TimeSpan.TicksPerMicrosecond)),
            EventType = eventType,
            SubjectType = subjectType,
            SubjectId = subjectId,
            Actor = actor,
            CorrelationId = correlationId,
            Payload = payload,
        };

    internal void Seal(long chainSequence, string previousHash, DateTimeOffset sealedAt)
    {
        if (IsSealed)
        {
            throw new InvalidOperationException($"Audit record {Id} is already sealed.");
        }

        ChainSequence = chainSequence;
        PreviousHash = previousHash;
        Hash = AuditChain.ComputeHash(this, chainSequence, previousHash);
        SealedAt = sealedAt;
    }
}
