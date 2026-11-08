namespace WalletPK.Identity.Application.Registration;

/// <summary>Registration settings, from the "Registration" configuration section.</summary>
public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";

    /// <summary>How long the user has, after verifying the OTP, to choose an MPIN.</summary>
    public int TokenTimeToLiveSeconds { get; set; } = 600;
}
