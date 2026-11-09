namespace WalletPK.Web.Idempotency;

/// <summary>A response saved so a repeated request can get exactly the same answer.</summary>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="ContentType">Content-Type header, if any.</param>
/// <param name="Location">Location header (for 201 Created), if any.</param>
/// <param name="Body">Response body bytes.</param>
public sealed record StoredResponse(int StatusCode, string? ContentType, string? Location, byte[] Body);

public enum IdempotencyStatus
{
    /// <summary>First time this key is seen: the caller now owns it and must complete or abandon it.</summary>
    Started = 0,

    /// <summary>The same request is still being processed (for example a double tap): try again shortly.</summary>
    InProgress = 1,

    /// <summary>Already done: replay <see cref="IdempotencyBegin.Response"/>.</summary>
    Completed = 2,

    /// <summary>The key was used before with a different request body: refuse.</summary>
    Mismatch = 3,
}

/// <param name="Status">What the store decided.</param>
/// <param name="Response">The saved response when <see cref="IdempotencyStatus.Completed"/>.</param>
public sealed record IdempotencyBegin(IdempotencyStatus Status, StoredResponse? Response = null);

/// <summary>
/// Remembers idempotency keys and their responses. Implementations must make <see cref="TryBeginAsync"/>
/// atomic, so two identical requests arriving together cannot both start.
/// </summary>
public interface IIdempotencyStore
{
    /// <param name="key">The scoped key (user + route + client key).</param>
    /// <param name="requestHash">SHA-256 of the request body, to detect a key reused for a different request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IdempotencyBegin> TryBeginAsync(string key, string requestHash, CancellationToken cancellationToken);

    /// <summary>Saves the response of a started request, so retries get it back.</summary>
    Task CompleteAsync(string key, StoredResponse response, CancellationToken cancellationToken);

    /// <summary>Forgets a started request that failed (5xx), so the client may retry it for real.</summary>
    Task AbandonAsync(string key, CancellationToken cancellationToken);
}
