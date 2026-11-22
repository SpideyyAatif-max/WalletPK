namespace WalletPK.Contracts.Ledger;

/// <summary>
/// The one-line title of a wallet entry, as statements and live updates show it: "Sent to +92300*****67",
/// "Received from ...", "Cash-in", "Cash-out", otherwise the journal's description. The Ledger uses it for
/// statements and the Notifications service for live pushes, so both always read the same.
/// </summary>
public static class WalletEntryTitles
{
    /// <param name="journalType">As in <see cref="WalletEntryPosted.JournalType"/>, for example "P2PTransfer".</param>
    /// <param name="description">The journal's description (the fallback).</param>
    /// <param name="direction">"Debit" (money out of the wallet) or "Credit".</param>
    /// <param name="counterpartyWalletNumber">The other wallet (E.164), shown masked.</param>
    public static string For(string journalType, string description, string direction, string? counterpartyWalletNumber) =>
        (journalType, counterpartyWalletNumber, direction) switch
        {
            ("P2PTransfer", { } other, "Debit") => $"Sent to {Mask(other)}",
            ("P2PTransfer", { } other, "Credit") => $"Received from {Mask(other)}",
            ("CashIn", _, _) => "Cash-in",
            ("CashOut", _, _) => "Cash-out",
            _ => description,
        };

    /// <summary>"+923001234567" as "+92300*****67", the way mobile numbers are shown to other users.</summary>
    public static string Mask(string e164)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(e164);
        return e164.Length < 8 ? "***" : $"{e164[..6]}*****{e164[^2..]}";
    }
}
