using WalletPK.Common.Results;

namespace WalletPK.Identity.Application.Abstractions;

public static class UnitOfWorkExtensions
{
    /// <summary>Saves, turning a lost optimistic-concurrency race into <paramref name="onConflict"/> instead of an exception.</summary>
    public static async Task<Result> TrySaveChangesAsync(this IUnitOfWork unitOfWork, Error onConflict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(onConflict);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (Exception exception) when (exception is ConcurrencyConflictException or UniqueConstraintViolationException)
        {
            return onConflict;
        }
    }
}
