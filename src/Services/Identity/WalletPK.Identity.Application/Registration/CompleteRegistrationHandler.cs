using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Registration;

/// <summary>Creates the account: validates the MPIN, consumes the registration token and saves the user.</summary>
public sealed class CompleteRegistrationHandler(
    IRegistrationTokenStore tokens,
    IUserRepository users,
    IUnitOfWork unitOfWork,
    IMpinHasher mpinHasher,
    TimeProvider timeProvider)
{
    public async Task<Result<RegisteredUser>> HandleAsync(CompleteRegistrationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Validate the MPIN before consuming the token, so a typo does not force the user to start over.
        if (!string.Equals(command.Mpin, command.ConfirmMpin, StringComparison.Ordinal))
        {
            return Error.Validation("mpin.mismatch", "The two MPINs do not match.");
        }

        var mpinCheck = MpinPolicy.Validate(command.Mpin);
        if (mpinCheck.IsFailure)
        {
            return mpinCheck.Error;
        }

        if (string.IsNullOrWhiteSpace(command.RegistrationToken))
        {
            return RegistrationErrors.SessionExpired;
        }

        var phone = await tokens.ConsumeAsync(command.RegistrationToken, cancellationToken);
        if (phone is null)
        {
            return RegistrationErrors.SessionExpired;
        }

        if (await users.ExistsByPhoneAsync(phone, cancellationToken))
        {
            return RegistrationErrors.AlreadyRegistered;
        }

        var user = User.Register(phone, mpinHasher.Hash(command.Mpin!), timeProvider.GetUtcNow());
        users.Add(user);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // Two sign-ups for the same number at the same moment: the database let only one through.
            return RegistrationErrors.AlreadyRegistered;
        }

        return new RegisteredUser(user.Id, user.PhoneNumber.Masked, user.KycTier, user.CreatedAt);
    }
}
