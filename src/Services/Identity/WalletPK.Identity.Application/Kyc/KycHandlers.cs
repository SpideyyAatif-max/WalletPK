using WalletPK.Common.Identifiers;
using WalletPK.Common.Results;
using WalletPK.Common.Time;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Account;
using WalletPK.Identity.Application.Authentication;
using WalletPK.Identity.Domain.Kyc;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Kyc;

/// <summary>
/// KYC upgrade L0 → L1:
/// <list type="number">
/// <item>Validate the CNIC, name and age (18+).</item>
/// <item>Refuse a CNIC that already verifies another account (looked up by blind index, never by the number).</item>
/// <item>Ask NADRA to confirm the identity.</item>
/// <item>Store the profile with the CNIC encrypted (AES-256-GCM, bound to this user) and raise the tier,
/// in one transaction that also writes the <c>identity.kyc_upgraded</c> event to the outbox.</item>
/// </list>
/// The current access token still says L0; the app refreshes its session to get one that says L1.
/// </summary>
public sealed class SubmitKycHandler(
    IUserRepository users,
    IKycProfileRepository profiles,
    IUnitOfWork unitOfWork,
    INadraClient nadra,
    IFieldEncryptor encryptor,
    IBlindIndex blindIndex,
    TimeProvider timeProvider)
{
    public const int MinimumAge = 18;

    public async Task<Result<KycStatus>> HandleAsync(UserId userId, SubmitKycCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var cnic = Cnic.Create(command.Cnic);
        if (cnic.IsFailure)
        {
            return cnic.Error;
        }

        var fullName = NormaliseName(command.FullName);
        if (fullName is null)
        {
            return KycErrors.NameInvalid;
        }

        if (command.DateOfBirth is not { } dateOfBirth)
        {
            return KycErrors.DateOfBirthRequired;
        }

        if (dateOfBirth.AddYears(MinimumAge) > timeProvider.GetBusinessDate())
        {
            return KycErrors.Underage;
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AccountErrors.UserNotFound;
        }

        if (user.KycTier >= KycTier.L1)
        {
            return UserErrors.AlreadyAtTier(user.KycTier);
        }

        var lookupHash = blindIndex.Compute(cnic.Value.Value);
        if (await profiles.ExistsByCnicLookupAsync(lookupHash, cancellationToken))
        {
            return KycErrors.CnicInUse;
        }

        var verification = await nadra.VerifyAsync(cnic.Value, fullName, dateOfBirth, cancellationToken);
        switch (verification.Outcome)
        {
            case NadraOutcome.Verified:
                break;
            case NadraOutcome.Unavailable:
                return KycErrors.NadraUnavailable;
            default:
                return KycErrors.NotVerified;
        }

        var now = timeProvider.GetUtcNow();
        var profile = KycProfile.CreateVerified(
            user.Id,
            encryptor.Encrypt(cnic.Value.Value, associatedData: user.Id.ToString()),
            lookupHash,
            cnic.Value,
            fullName,
            dateOfBirth,
            verification.Reference ?? "unknown",
            now);
        profiles.Add(profile);

        var upgraded = user.UpgradeKycTier(KycTier.L1, now);
        if (upgraded.IsFailure)
        {
            return upgraded.Error;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // The same CNIC was submitted for two accounts at the same moment; the unique index let one through.
            return KycErrors.CnicInUse;
        }
        catch (ConcurrencyConflictException)
        {
            return AuthErrors.ConcurrentRequest;
        }

        return new KycStatus(user.KycTier, true, profile.CnicMasked, profile.FullName, profile.VerifiedAt);
    }

    /// <summary>Trims and collapses spaces; letters, spaces, dots, apostrophes and hyphens only, 3–100 characters.</summary>
    internal static string? NormaliseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var collapsed = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var valid = collapsed.Length is >= 3 and <= KycProfile.FullNameMaxLength &&
            collapsed.All(c => char.IsLetter(c) || c is ' ' or '.' or '\'' or '-');

        return valid ? collapsed : null;
    }
}

/// <summary>Returns the user's current KYC state.</summary>
public sealed class GetKycStatusHandler(IUserRepository users, IKycProfileRepository profiles)
{
    public async Task<Result<KycStatus>> HandleAsync(UserId userId, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AccountErrors.UserNotFound;
        }

        var profile = await profiles.GetByUserIdAsync(userId, cancellationToken);
        return new KycStatus(user.KycTier, profile is not null, profile?.CnicMasked, profile?.FullName, profile?.VerifiedAt);
    }
}
