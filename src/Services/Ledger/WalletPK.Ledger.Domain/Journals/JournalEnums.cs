namespace WalletPK.Ledger.Domain.Journals;

public enum EntryDirection
{
    Debit = 0,
    Credit = 1,
}

/// <summary>What a journal transaction records; drives reporting and, later, fee rules.</summary>
public enum JournalType
{
    /// <summary>Money enters a wallet from a bank or agent.</summary>
    CashIn = 0,

    /// <summary>Money leaves a wallet to a bank or agent.</summary>
    CashOut = 1,

    /// <summary>Wallet to wallet.</summary>
    P2PTransfer = 2,

    BankTransfer = 3,
    BillPayment = 4,
    MobileTopup = 5,
    MerchantPayment = 6,

    /// <summary>Undoes an earlier journal with the opposite entries (entries are never edited or deleted).</summary>
    Reversal = 7,

    /// <summary>Manual correction by operations, always with a reason.</summary>
    Adjustment = 8,
}
