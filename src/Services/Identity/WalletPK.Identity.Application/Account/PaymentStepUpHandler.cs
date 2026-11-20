using WalletPK.Common.Identifiers;
using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Authentication;
using WalletPK.Identity.Application.Otp;

namespace WalletPK.Identity.Application.Account;

/// <param name="ValidUntil">Until when the next payment that needs a code goes through.</param>
public sealed record PaymentStepUpResult(DateTimeOffset ValidUntil);

/// <summary>
/// Step-up OTP for payments (M10.4, FR-5.1): when the fraud engine answers "step up" a payment is refused
/// with <c>fraud.step_up_required</c>; the app asks for a code here, the user types it, and the payment is
/// sent again. A confirmed code covers one payment from the same device for a few minutes.
/// </summary>
public sealed class PaymentStepUpHandler(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    OtpService otp,
    TimeProvider timeProvider)
{
    /// <summary>How long a confirmation waits for its payment.</summary>
    public static TimeSpan ValidFor { get; } = TimeSpan.FromMinutes(5);

    public async Task<Result<OtpIssued>> StartAsync(UserId userId, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AccountErrors.UserNotFound;
        }

        return user.CanTransact(timeProvider.GetUtcNow())
            ? await otp.IssueAsync(OtpPurpose.PaymentStepUp, user.PhoneNumber, cancellationToken)
            : AccountErrors.CannotTransact;
    }

    public async Task<Result<PaymentStepUpResult>> VerifyAsync(UserId userId, Guid? deviceId, string? code, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AccountErrors.UserNotFound;
        }

        var now = timeProvider.GetUtcNow();
        if (!user.CanTransact(now))
        {
            return AccountErrors.CannotTransact;
        }

        // OtpService enforces expiry, the attempt limit and single use.
        var verified = await otp.VerifyAsync(OtpPurpose.PaymentStepUp, user.PhoneNumber, code, cancellationToken);
        if (verified.IsFailure)
        {
            return verified.Error;
        }

        user.ConfirmPaymentStepUp(deviceId, now, ValidFor);
        var saved = await unitOfWork.TrySaveChangesAsync(AuthErrors.ConcurrentRequest, cancellationToken);
        return saved.IsFailure ? saved.Error : new PaymentStepUpResult(now + ValidFor);
    }
}
