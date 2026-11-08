using WalletPK.Common.Results;
using WalletPK.Identity.Domain.Devices;

namespace WalletPK.Identity.Application.Authentication;

/// <summary>A validated installation ID and display name sent by the app at login.</summary>
public sealed record DeviceInput(string InstallationId, string Name)
{
    public const int InstallationIdMinLength = 16;
    public const string DefaultName = "Unknown device";

    /// <summary>
    /// The installation ID must be 16–128 characters of letters, digits, '-' or '_' (a UUID fits).
    /// A missing name becomes "Unknown device"; a long one is shortened.
    /// </summary>
    public static Result<DeviceInput> Create(string? installationId, string? name)
    {
        var id = installationId?.Trim();
        if (string.IsNullOrEmpty(id) ||
            id.Length < InstallationIdMinLength ||
            id.Length > Device.InstallationIdMaxLength ||
            !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return AuthErrors.InvalidDevice;
        }

        var displayName = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        if (displayName.Length > Device.NameMaxLength)
        {
            displayName = displayName[..Device.NameMaxLength];
        }

        return new DeviceInput(id, displayName);
    }
}
