namespace WalletPK.Ledger.Infrastructure.Idempotency;

/// <summary>One idempotency key: claimed while its request runs, then holding the response for replays.</summary>
public sealed class IdempotencyRecord
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";

    public string Key { get; set; } = null!;

    public string RequestHash { get; set; } = null!;

    public string State { get; set; } = InProgress;

    public int? StatusCode { get; set; }

    public string? ContentType { get; set; }

    public string? Location { get; set; }

    public byte[]? ResponseBody { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>In progress: when a crashed request's claim may be taken over. Completed: when it is purged.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
