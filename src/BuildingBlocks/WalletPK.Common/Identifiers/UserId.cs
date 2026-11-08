using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace WalletPK.Common.Identifiers;

/// <summary>Identifies a WalletPK user (customer, merchant, agent or staff).</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<UserId>))]
public readonly record struct UserId(Guid Value) : IStronglyTypedId<UserId>, IParsable<UserId>
{
    public static UserId Empty => default;

    /// <summary>Creates a new time-ordered (UUIDv7) identifier.</summary>
    public static UserId New() => new(Guid.CreateVersion7());

    public static UserId From(Guid value) => new(value);

    public static UserId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out UserId result)
    {
        if (Guid.TryParse(s, provider, out var guid))
        {
            result = new UserId(guid);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}
