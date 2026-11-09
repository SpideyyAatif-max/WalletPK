using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using WalletPK.Common.Identifiers;

namespace WalletPK.Ledger.Domain.Accounts;

/// <summary>Identifies one ledger account: a customer, merchant or agent wallet, or a system account shard.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<AccountId>))]
public readonly record struct AccountId(Guid Value) : IStronglyTypedId<AccountId>, IParsable<AccountId>
{
    public static AccountId New() => new(Guid.CreateVersion7());

    public static AccountId From(Guid value) => new(value);

    /// <summary>A wallet's account ID is its wallet ID.</summary>
    public static AccountId From(WalletId walletId) => new(walletId.Value);

    public static AccountId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out AccountId result)
    {
        if (Guid.TryParse(s, provider, out var guid))
        {
            result = new AccountId(guid);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}
