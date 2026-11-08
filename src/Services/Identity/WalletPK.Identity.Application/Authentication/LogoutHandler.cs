using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;

namespace WalletPK.Identity.Application.Authentication;

/// <summary>
/// Ends the session the refresh token belongs to (its whole family). Always succeeds, so the
/// response does not reveal whether a token was valid. The current access token stays valid
/// until it expires (at most 15 minutes); the app simply discards it.
/// </summary>
public sealed class LogoutHandler(IRefreshTokenRepository refreshTokens, IUnitOfWork unitOfWork, TimeProvider timeProvider)
{
    public async Task<Result> HandleAsync(LogoutCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return Result.Success();
        }

        var stored = await refreshTokens.GetByHashAsync(RefreshTokenSecret.Hash(command.RefreshToken), cancellationToken);
        if (stored is null)
        {
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow();
        foreach (var token in await refreshTokens.ListByFamilyAsync(stored.FamilyId, cancellationToken))
        {
            token.Revoke(now);
        }

        await unitOfWork.TrySaveChangesAsync(AuthErrors.ConcurrentRequest, cancellationToken);
        return Result.Success();
    }
}
