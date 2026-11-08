using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Otp;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Registration;

/// <summary>Validates the number, makes sure it has no account yet, and sends a registration OTP.</summary>
public sealed class StartRegistrationHandler(IUserRepository users, OtpService otp)
{
    public async Task<Result<OtpChallenge>> HandleAsync(StartRegistrationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var phone = PhoneNumber.Create(command.PhoneNumber);
        if (phone.IsFailure)
        {
            return phone.Error;
        }

        if (await users.ExistsByPhoneAsync(phone.Value, cancellationToken))
        {
            return RegistrationErrors.AlreadyRegistered;
        }

        var issued = await otp.IssueAsync(OtpPurpose.Registration, phone.Value, cancellationToken);
        return issued.Map(sent => new OtpChallenge(phone.Value.Masked, sent.ExpiresInSeconds, sent.ResendAfterSeconds));
    }
}
