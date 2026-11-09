using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Wallets;

/// <summary>Send money from the signed-in user's wallet to another wallet, by mobile number.</summary>
/// <param name="SenderId">The signed-in user.</param>
/// <param name="DeviceId">The device used (for the new-device cooling-off limit).</param>
/// <param name="ToPhoneNumber">Recipient's mobile number, any common format.</param>
/// <param name="Amount">Amount to send (the fee comes on top).</param>
/// <param name="TransactionId">Derived from the request's Idempotency-Key, so a retry is the same transaction.</param>
public sealed record TransferCommand(UserId SenderId, string? DeviceId, string? ToPhoneNumber, Money Amount, TransactionId TransactionId);

/// <summary>The signed-in user's wallet.</summary>
/// <param name="WalletId">The wallet (ledger account) ID.</param>
/// <param name="WalletNumber">The mobile number people send money to.</param>
/// <param name="Type">Customer, merchant or agent wallet.</param>
/// <param name="Balance">Total balance.</param>
/// <param name="Held">Reserved by pending payments.</param>
/// <param name="Available">What can be spent now.</param>
/// <param name="Status">Active, Frozen or Closed.</param>
/// <param name="OpenedAt">When the wallet was opened.</param>
public sealed record WalletView(
    AccountId WalletId,
    string? WalletNumber,
    AccountType Type,
    Money Balance,
    Money Held,
    Money Available,
    AccountStatus Status,
    DateTimeOffset OpenedAt)
{
    public static WalletView From(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new WalletView(
            account.Id,
            account.Alias,
            account.Type,
            Money.FromPaisa(account.Balance),
            Money.FromPaisa(account.Held),
            Money.FromPaisa(account.Available),
            account.Status,
            account.CreatedAt);
    }
}

/// <param name="Wallet">The wallet.</param>
/// <param name="Created">False when the user already had one (opening is idempotent).</param>
public sealed record OpenWalletResult(WalletView Wallet, bool Created);

/// <summary>The receipt shown after a successful transfer.</summary>
/// <param name="TransactionId">The journal; quote it to support.</param>
/// <param name="ToWalletNumber">Recipient, masked.</param>
/// <param name="Amount">Amount received by the recipient.</param>
/// <param name="Fee">WalletPK's fee.</param>
/// <param name="Tax">Tax on the fee.</param>
/// <param name="TotalDebited">Amount + fee + tax.</param>
/// <param name="BalanceAfter">The sender's balance after the transfer.</param>
/// <param name="PostedAt">When it happened.</param>
public sealed record TransferReceipt(
    TransactionId TransactionId,
    string ToWalletNumber,
    Money Amount,
    Money Fee,
    Money Tax,
    Money TotalDebited,
    Money BalanceAfter,
    DateTimeOffset PostedAt);

/// <summary>One line of the wallet's statement, as shown in the app.</summary>
/// <param name="EntryId">Paging cursor.</param>
/// <param name="TransactionId">The journal.</param>
/// <param name="Type">What happened.</param>
/// <param name="Title">For example "Sent to +92300*****67" or "Cash-in".</param>
/// <param name="Direction">"Out" or "In".</param>
/// <param name="Amount">The amount.</param>
/// <param name="BalanceAfter">Balance right after.</param>
/// <param name="PostedAt">When.</param>
public sealed record StatementLine(
    long EntryId,
    TransactionId TransactionId,
    JournalType Type,
    string Title,
    string Direction,
    Money Amount,
    Money BalanceAfter,
    DateTimeOffset PostedAt);

/// <param name="Items">Newest first.</param>
/// <param name="NextBefore">Pass as "before" to get the next (older) page; null on the last page.</param>
public sealed record StatementPage(IReadOnlyList<StatementLine> Items, long? NextBefore);
