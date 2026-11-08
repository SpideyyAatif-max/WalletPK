namespace WalletPK.Identity.Application.Abstractions;

/// <summary>Commits all changes of one use case in a single database transaction.</summary>
public interface IUnitOfWork
{
    /// <exception cref="UniqueConstraintViolationException">A unique rule (such as one account per phone number) was broken.</exception>
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
