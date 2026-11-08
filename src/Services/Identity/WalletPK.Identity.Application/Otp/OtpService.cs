using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Otp;

/// <summary>Result of sending a code: how long it is valid and when a new one may be requested.</summary>
public sealed record OtpIssued(int ExpiresInSeconds, int ResendAfterSeconds);

/// <summary>
/// Issues and checks one-time passwords: cryptographically random codes, stored only as a keyed hash,
/// valid for a short time, limited wrong guesses, single use, and a cooldown between resends.
/// </summary>
public sealed class OtpService(IOtpStore store, ISmsSender smsSender, OtpOptions options)
{
    public async Task<Result<OtpIssued>> IssueAsync(OtpPurpose purpose, PhoneNumber phoneNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(phoneNumber);

        var waitLeft = await store.TryBeginCooldownAsync(
            purpose, phoneNumber, TimeSpan.FromSeconds(options.ResendCooldownSeconds), cancellationToken);
        if (waitLeft is not null)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(waitLeft.Value.TotalSeconds));
            return Error.TooManyRequests("otp.resend_too_soon", $"Please wait {seconds} seconds before requesting a new code.");
        }

        var code = GenerateCode(options.Length);
        await store.SaveAsync(purpose, phoneNumber, ComputeCodeHash(code), TimeSpan.FromSeconds(options.TimeToLiveSeconds), cancellationToken);

        var minutes = Math.Max(1, options.TimeToLiveSeconds / 60);
        await smsSender.SendAsync(
            phoneNumber,
            $"Your WalletPK code is {code}. It expires in {minutes} minute(s). Never share this code with anyone.",
            cancellationToken);

        return new OtpIssued(options.TimeToLiveSeconds, options.ResendCooldownSeconds);
    }

    public async Task<Result> VerifyAsync(OtpPurpose purpose, PhoneNumber phoneNumber, string? code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(phoneNumber);

        var record = await store.GetAsync(purpose, phoneNumber, cancellationToken);
        if (record is null)
        {
            return Error.Validation("otp.expired", "This code has expired or was never sent. Request a new code.");
        }

        if (string.IsNullOrWhiteSpace(code) || !Matches(code.Trim(), record.CodeHash))
        {
            var failures = await store.IncrementFailedAttemptsAsync(purpose, phoneNumber, cancellationToken);
            if (failures >= options.MaxAttempts)
            {
                await store.DeleteAsync(purpose, phoneNumber, cancellationToken);
                return Error.TooManyRequests("otp.too_many_attempts", "Too many incorrect codes. Request a new code.");
            }

            var left = options.MaxAttempts - failures;
            return Error.Validation("otp.incorrect", $"Incorrect code. {left} attempt(s) left.");
        }

        // A code can be used only once.
        await store.DeleteAsync(purpose, phoneNumber, cancellationToken);
        return Result.Success();
    }

    internal static string GenerateCode(int length)
    {
        var upperBound = (int)Math.Pow(10, length);
        return RandomNumberGenerator.GetInt32(0, upperBound).ToString("D" + length.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    internal string ComputeCodeHash(string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Pepper), Encoding.UTF8.GetBytes(code)));

    private bool Matches(string code, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(ComputeCodeHash(code)),
            Encoding.ASCII.GetBytes(expectedHash));
}
