namespace WalletPK.Identity.Application.Abstractions;

/// <summary>Commits all changes of one use case in a single database transaction.</summary>
public interface IUnitOfWork
{
    /// <exception cref="UniqueConstraintViolationException">A unique rule (such as one account per phone number) was broken.</exception>
    /// <exception cref="ConcurrencyConflictException">Another request changed the same row first.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when a save breaks a unique constraint, for example two sign-ups for the same number at
/// the same moment. The database is the final judge; the use case turns this into a 409 Conflict.
/// </summary>
public sealed class UniqueConstraintViolationException : Exception
{
    public UniqueConstraintViolationException()
    {
    }

    public UniqueConstraintViolationException(string message)
        : base(message)
    {
    }

    public UniqueConstraintViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string? ConstraintName { get; init; }
}

/// <summary>
/// Thrown when a row changed between being read and being saved (optimistic concurrency).
/// For example: two refreshes with the same refresh token, or two wrong MPINs sent in parallel
/// to dodge the attempt counter. Only one of them wins; the other is rejected.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
