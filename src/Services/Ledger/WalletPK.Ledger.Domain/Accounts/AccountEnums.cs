namespace WalletPK.Ledger.Domain.Accounts;

public enum AccountType
{
    CustomerWallet = 0,
    MerchantWallet = 1,
    AgentWallet = 2,

    /// <summary>WalletPK's own books: fees earned, money at banks, money owed to billers, tax, suspense.</summary>
    System = 3,
}

/// <summary>
/// Which side increases the account (double-entry bookkeeping).
/// <list type="bullet">
/// <item><b>Credit-normal</b>: what WalletPK owes or earns. Customer wallets (WalletPK owes the customer
/// that money), fee income, tax payable, biller settlement. A credit increases the balance.</item>
/// <item><b>Debit-normal</b>: what WalletPK owns. Money held at partner banks (bank float). A debit increases it.</item>
/// </list>
/// </summary>
public enum NormalBalance
{
    Debit = 0,
    Credit = 1,
}

public enum AccountStatus
{
    Active = 0,

    /// <summary>Can receive money but nothing can leave it (compliance hold).</summary>
    Frozen = 1,

    /// <summary>No postings at all.</summary>
    Closed = 2,
}
