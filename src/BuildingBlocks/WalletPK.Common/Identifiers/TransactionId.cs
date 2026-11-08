using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace WalletPK.Common.Identifiers;

/// <summary>Identifies one business transaction (a transfer, bill payment, top-up...) across all services.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<TransactionId>))]
public readonly record struct TransactionId(Guid Value) : IStronglyTypedId<TransactionId>, IParsable<TransactionId>
{
    public static TransactionId Empty => default;

    /// <summary>Creates a new time-ordered (UUIDv7) identifier.</summary>
    public static TransactionId New() => new(Guid.CreateVersion7());

    public static TransactionId From(Guid value) => new(value);

    public static TransactionId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out TransactionId result)
    {
        if (Guid.TryParse(s, provider, out var guid))
        {
            result = new TransactionId(guid);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}
