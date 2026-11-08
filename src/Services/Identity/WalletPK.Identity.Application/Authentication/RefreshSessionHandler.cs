using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;

namespace WalletPK.Identity.Application.Authentication;

/// <summary>
/// Exchanges a refresh token for a new access + refresh token pair (rotation).
/// Presenting a refresh token that was already exchanged means it was copied: the whole
/// session family is revoked, logging out both the thief and the real user.
/// </summary>
public sealed class RefreshSessionHandler(
    IRefreshTokenRepository refreshTokens,
    IUserRepository users,
    IDeviceRepository devices,
    IUnitOfWork unitOfWork,
    SessionIssuer sessions,
    TimeProvider timeProvider)
{
    public async Task<Result<AuthTokens>> HandleAsync(RefreshSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return AuthErrors.InvalidRefreshToken;
        }

        var now = timeProvider.GetUtcNow();
        var stored = await refreshTokens.GetByHashAsync(RefreshTokenSecret.Hash(command.RefreshToken), cancellationToken);
        if (stored is null)
        {
            return AuthErrors.InvalidRefreshToken;
        }

        if (stored.IsConsumed)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            return AuthErrors.RefreshTokenReused;
        }

        if (!stored.IsActive(now))
        {
            return AuthErrors.InvalidRefreshToken;
        }

        var device = await devices.GetByIdAsync(stored.DeviceId, cancellationToken);
        var user = await users.GetByIdAsync(stored.UserId, cancellationToken);
        if (user is null || device is not { IsTrusted: true })
        {
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            return AuthErrors.InvalidRefreshToken;
        }

        // A locked or frozen account cannot extend its sessions.
        var allowed = user.EnsureCanLogIn(now);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        stored.Consume(now);
        device.MarkSeen(now);
        var tokens = sessions.Continue(user, device, stored.FamilyId, now);

        // Two refreshes with the same token at the same moment: xmin lets only one consume it.
        var saved = await unitOfWork.TrySaveChangesAsync(AuthErrors.InvalidRefreshToken, cancellationToken);
        return saved.IsFailure ? saved.Error : tokens;
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var token in await refreshTokens.ListByFamilyAsync(familyId, cancellationToken))
        {
            token.Revoke(now);
        }

        // If another request touched the family at the same moment, it is revoked by that request or the next one.
        await unitOfWork.TrySaveChangesAsync(AuthErrors.InvalidRefreshToken, cancellationToken);
    }
}
