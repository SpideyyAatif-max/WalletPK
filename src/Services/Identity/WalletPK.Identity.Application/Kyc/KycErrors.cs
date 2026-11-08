using WalletPK.Common.Results;

namespace WalletPK.Identity.Application.Kyc;

public static class KycErrors
{
    public static Error NameInvalid { get; } =
        Error.Validation("kyc.name_invalid", "Enter your full name exactly as it appears on your CNIC.");

    public static Error DateOfBirthRequired { get; } =
        Error.Validation("kyc.date_of_birth_required", "Enter your date of birth.");

    public static Error Underage { get; } =
        Error.BusinessRule("kyc.underage", "You must be at least 18 years old to verify a wallet.");

    /// <summary>One CNIC can verify only one WalletPK account.</summary>
    public static Error CnicInUse { get; } =
        Error.Conflict("kyc.cnic_already_used", "This CNIC is already linked to another WalletPK account.");

    public static Error NotVerified { get; } =
        Error.BusinessRule("kyc.not_verified", "NADRA could not verify these details. Check your CNIC number, name and date of birth.");

    public static Error NadraUnavailable { get; } =
        Error.Unavailable("kyc.nadra_unavailable", "Identity verification is temporarily unavailable. Please try again in a few minutes.");
}
