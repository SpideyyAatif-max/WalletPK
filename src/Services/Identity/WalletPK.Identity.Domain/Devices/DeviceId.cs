using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using WalletPK.Common.Identifiers;

namespace WalletPK.Identity.Domain.Devices;

/// <summary>Identifies one trusted device (phone) of a user. Travels in the access token's "device_id" claim.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<DeviceId>))]
public readonly record struct DeviceId(Guid Value) : IStronglyTypedId<DeviceId>, IParsable<DeviceId>
{
    public static DeviceId New() => new(Guid.CreateVersion7());

    public static DeviceId From(Guid value) => new(value);

    public static DeviceId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out DeviceId result)
    {
        if (Guid.TryParse(s, provider, out var guid))
        {
            result = new DeviceId(guid);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}
