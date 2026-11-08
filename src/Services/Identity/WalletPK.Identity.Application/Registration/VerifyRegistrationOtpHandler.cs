using System.Security.Cryptography;
using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Otp;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Registration;

/// <summary>Checks the OTP and, if correct, issues a short-lived single-use registration token.</summary>
public sealed class VerifyRegistrationOtpHandler(OtpService otp, IRegistrationTokenStore tokens, RegistrationOptions options)
{
    public async Task<Result<RegistrationTicket>> HandleAsync(VerifyRegistrationOtpCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var phone = PhoneNumber.Create(command.PhoneNumber);
        if (phone.IsFailure)
        {
            return phone.Error;
        }

        var verified = await otp.VerifyAsync(OtpPurpose.Registration, phone.Value, command.Code, cancellationToken);
        if (verified.IsFailure)
        {
            return verified.Error;
        }

        // 256 random bits: unguessable, and useless after one use or 10 minutes.
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await tokens.SaveAsync(token, phone.Value, TimeSpan.FromSeconds(options.TokenTimeToLiveSeconds), cancellationToken);

        return new RegistrationTicket(token, options.TokenTimeToLiveSeconds);
    }
}
