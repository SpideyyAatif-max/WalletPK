using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Registration;

/// <summary>Step 1: the user enters a phone number and receives an OTP.</summary>
public sealed record StartRegistrationCommand(string? PhoneNumber);

/// <summary>Step 2: the user enters the OTP.</summary>
public sealed record VerifyRegistrationOtpCommand(string? PhoneNumber, string? Code);

/// <summary>Step 3: the user chooses an MPIN; this creates the account.</summary>
public sealed record CompleteRegistrationCommand(string? RegistrationToken, string? Mpin, string? ConfirmMpin);

/// <summary>Response to step 1.</summary>
/// <param name="PhoneNumber">Masked, for example "+92300*****67".</param>
public sealed record OtpChallenge(string PhoneNumber, int ExpiresInSeconds, int ResendAfterSeconds);

/// <summary>Response to step 2: proof the phone was verified, required for step 3.</summary>
public sealed record RegistrationTicket(string RegistrationToken, int ExpiresInSeconds);

/// <summary>Response to step 3.</summary>
public sealed record RegisteredUser(UserId UserId, string PhoneNumber, KycTier KycTier, DateTimeOffset RegisteredAt);
