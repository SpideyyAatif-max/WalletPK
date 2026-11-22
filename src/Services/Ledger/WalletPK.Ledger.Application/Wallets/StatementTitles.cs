using WalletPK.Contracts.Ledger;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Wallets;

/// <summary>
/// The one-line title of a statement entry, as the app shows it. The wording lives in
/// <see cref="WalletEntryTitles"/>, shared with the Notifications service's live pushes.
/// </summary>
public static class StatementTitles
{
    /// <param name="type">What happened.</param>
    /// <param name="description">The journal's description (the fallback).</param>
    /// <param name="direction">Debit = money out of the wallet.</param>
    /// <param name="counterpartyWalletNumber">The other wallet (E.164), shown masked.</param>
    public static string For(JournalType type, string description, EntryDirection direction, string? counterpartyWalletNumber) =>
        WalletEntryTitles.For(type.ToString(), description, direction.ToString(), counterpartyWalletNumber);
}
