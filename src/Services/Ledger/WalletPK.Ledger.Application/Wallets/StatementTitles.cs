using WalletPK.Common.Primitives;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Wallets;

/// <summary>The one-line title of a statement entry, as the app shows it.</summary>
public static class StatementTitles
{
    /// <param name="type">What happened.</param>
    /// <param name="description">The journal's description (the fallback).</param>
    /// <param name="direction">Debit = money out of the wallet.</param>
    /// <param name="counterpartyWalletNumber">The other wallet (E.164), shown masked.</param>
    public static string For(JournalType type, string description, EntryDirection direction, string? counterpartyWalletNumber) =>
        (type, counterpartyWalletNumber, direction) switch
        {
            (JournalType.P2PTransfer, { } other, EntryDirection.Debit) => $"Sent to {PakistaniMobileNumber.Mask(other)}",
            (JournalType.P2PTransfer, { } other, EntryDirection.Credit) => $"Received from {PakistaniMobileNumber.Mask(other)}",
            (JournalType.CashIn, _, _) => "Cash-in",
            (JournalType.CashOut, _, _) => "Cash-out",
            _ => description,
        };
}
