namespace WalletPK.Identity.Domain.Devices;

public enum DeviceStatus
{
    /// <summary>Verified with an OTP; can log in with the MPIN alone.</summary>
    Trusted = 0,

    /// <summary>Removed by the user (or by support); needs a new OTP to be trusted again.</summary>
    Revoked = 1,
}
