using Microsoft.AspNetCore.SignalR;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Web.Identity;

namespace WalletPK.Ledger.Api.Realtime;

/// <summary>
/// Live wallet updates for the signed-in customer (M4.7), at /hubs/wallet (through the gateway too).
/// The browser only listens: every new entry on one of the user's wallets arrives as
/// <see cref="IWalletClient.EntryPosted"/>. Connect with the access token (SignalR sends it as
/// ?access_token= on WebSockets). The full hub set with a Redis backplane comes in M11.1.
/// </summary>
public sealed class WalletHub : Hub<IWalletClient>
{
    public const string Path = "/hubs/wallet";
}

/// <summary>What the server calls on connected browsers.</summary>
public interface IWalletClient
{
    /// <summary>A wallet of the user got a new entry. Ignore it when <c>sequence</c> is not newer than what is shown.</summary>
    Task EntryPosted(LiveWalletEntry entry);
}

/// <summary>One new wallet entry, pushed live.</summary>
/// <param name="WalletId">The wallet.</param>
/// <param name="WalletType">"CustomerWallet", "MerchantWallet" or "AgentWallet".</param>
/// <param name="Sequence">The entry's number in the wallet's history; the balance below is as of this entry.</param>
/// <param name="Balance">The wallet's balance right after the entry.</param>
/// <param name="Entry">The entry as a statement line (same shape as GET /api/wallets/me/transactions).</param>
public sealed record LiveWalletEntry(AccountId WalletId, string WalletType, long Sequence, Money Balance, StatementLine Entry);

/// <summary>SignalR addresses users by the access token's "sub" claim, the same ID the rest of the API uses.</summary>
internal sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection?.User.FindFirst(WalletPkClaimTypes.Subject)?.Value;
}
