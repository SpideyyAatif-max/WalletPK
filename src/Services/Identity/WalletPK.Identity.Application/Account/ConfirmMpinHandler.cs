using WalletPK.Common.Identifiers;
using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Authentication;
using WalletPK.Identity.Domain.Devices;

namespace WalletPK.Identity.Application.Account;

/// <param name="ConfirmationToken">Send it as X-Mpin-Confirmation with the payment.</param>
/// <param name="ExpiresAt">Until when the payment must be sent.</param>
public sealed record MpinConfirmationResult(string ConfirmationToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Confirms a payment with the MPIN (proposal 12.1, the send-money confirmation screen). The MPIN is checked
/// like at login: a locked or frozen account is refused first, and wrong MPINs count towards the same
/// lockout (5 in a row: 30 minutes), so the confirmation screen cannot be used to guess it.
/// </summary>
public sealed class ConfirmMpinHandler(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    IMpinHasher mpinHasher,
    IMpinConfirmationIssuer issuer,
    AuthOptions options,
    TimeProvider timeProvider)
{
    public const int MaxBindingLength = 100;

    public async Task<Result<MpinConfirmationResult>> HandleAsync(UserId userId, DeviceId? deviceId, string? mpin, string? binding, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(binding) || binding.Length > MaxBindingLength)
        {
            return AccountErrors.BindingRequired;
        }

        if (deviceId is not { } device)
        {
            return AccountErrors.DeviceRequired;
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AccountErrors.UserNotFound;
        }

        var now = timeProvider.GetUtcNow();
        var allowed = user.EnsureCanLogIn(now);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        if (string.IsNullOrEmpty(mpin) || !mpinHasher.Verify(mpin, user.MpinHash))
        {
            var lockedNow = user.RecordFailedMpin(now, options.MaxFailedMpinAttempts, options.LockoutDuration);

            // xmin makes parallel guesses conflict, so the counter cannot be raced.
            var failed = await unitOfWork.TrySaveChangesAsync(AuthErrors.ConcurrentRequest, cancellationToken);
            if (failed.IsFailure)
            {
                return failed.Error;
            }

            return lockedNow
                ? AuthErrors.LockedNow(options.LockoutMinutes)
                : AccountErrors.IncorrectMpin(options.MaxFailedMpinAttempts - user.FailedMpinAttempts);
        }

        if (!user.CanTransact(now))
        {
            return AccountErrors.CannotTransact;
        }

        user.RecordCorrectMpin(now);
        var saved = await unitOfWork.TrySaveChangesAsync(AuthErrors.ConcurrentRequest, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        var issued = issuer.Issue(userId, device, binding, now);
        return new MpinConfirmationResult(issued.Token, issued.ExpiresAt);
    }
}
