using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace WalletPK.Common.Identifiers;

/// <summary>Identifies a wallet (a ledger account owned by a user).</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<WalletId>))]
public readonly record struct WalletId(Guid Value) : IStronglyTypedId<WalletId>, IParsable<WalletId>
{
    public static WalletId Empty => default;

    /// <summary>Creates a new time-ordered (UUIDv7) identifier.</summary>
    public static WalletId New() => new(Guid.CreateVersion7());

    public static WalletId From(Guid value) => new(value);

    public static WalletId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out WalletId result)
    {
        if (Guid.TryParse(s, provider, out var guid))
        {
            result = new WalletId(guid);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}
