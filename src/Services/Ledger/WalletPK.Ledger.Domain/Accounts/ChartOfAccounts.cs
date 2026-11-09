using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WalletPK.Common.Identifiers;

namespace WalletPK.Ledger.Domain.Accounts;

/// <summary>A system account in the chart of accounts.</summary>
/// <param name="Code">Stable code used in postings, for example "FEE_INCOME".</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="NormalBalance">The side that increases it.</param>
/// <param name="Shards">
/// How many rows the account is split into. Almost every payment touches FEE_INCOME, so if it were one
/// row, every payment in the country would queue for that row's lock. Spread over 8 rows, 8 postings
/// can proceed at once; the account's real balance is the sum of its shards.
/// </param>
/// <param name="Description">What the account represents.</param>
public sealed record SystemAccountDefinition(string Code, string Name, NormalBalance NormalBalance, int Shards, string Description);

/// <summary>
/// WalletPK's chart of accounts. Customer, merchant and agent wallets are opened one per user; the
/// system accounts below exist once (in shards) and are created by the database migration.
/// </summary>
public static class ChartOfAccounts
{
    public static SystemAccountDefinition BankFloat { get; } = new(
        "BANK_FLOAT", "Bank float", NormalBalance.Debit, 4,
        "Money WalletPK holds at partner banks; backs every wallet balance. Asset.");

    public static SystemAccountDefinition FeeIncome { get; } = new(
        "FEE_INCOME", "Fee income", NormalBalance.Credit, 8,
        "Fees charged to customers. Revenue; the hottest account, so the most shards.");

    public static SystemAccountDefinition TaxPayable { get; } = new(
        "TAX_PAYABLE", "Tax payable", NormalBalance.Credit, 4,
        "Tax collected on fees (FED / withholding), owed to the government. Liability.");

    public static SystemAccountDefinition BillerSettlement { get; } = new(
        "BILLER_SETTLEMENT", "Biller settlement", NormalBalance.Credit, 4,
        "Bill payments collected but not yet paid out to utility companies. Liability.");

    public static SystemAccountDefinition Suspense { get; } = new(
        "SUSPENSE", "Suspense", NormalBalance.Debit, 1,
        "Money whose destination is not known yet (failed or unmatched transfers); cleared by reconciliation.");

    public static IReadOnlyList<SystemAccountDefinition> SystemAccounts { get; } =
        [BankFloat, FeeIncome, TaxPayable, BillerSettlement, Suspense];

    public static SystemAccountDefinition Get(string code) =>
        SystemAccounts.FirstOrDefault(definition => definition.Code == code)
        ?? throw new ArgumentException($"'{code}' is not a system account in the chart of accounts.", nameof(code));

    /// <summary>
    /// The fixed ID of a system account shard, derived from its code, so every environment (and the
    /// migration that creates them) agrees on it without a lookup.
    /// </summary>
    public static AccountId IdFor(string code, int shardIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"walletpk:ledger:system-account:{code}:{shardIndex}"));
        return AccountId.From(new Guid(hash.AsSpan(0, 16)));
    }

    /// <summary>
    /// Which shard of a system account a transaction uses. Derived from the transaction ID, so it is
    /// spread evenly and stays the same if the same transaction is retried.
    /// </summary>
    public static int ShardFor(SystemAccountDefinition definition, TransactionId transactionId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Shards == 1)
        {
            return 0;
        }

        Span<byte> bytes = stackalloc byte[16];
        transactionId.Value.TryWriteBytes(bytes);

        // The last bytes of a UUIDv7 are random; the first are a timestamp and would cluster.
        return (int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) % (uint)definition.Shards);
    }

    /// <summary>The system account shard a transaction posts to.</summary>
    public static AccountId ShardIdFor(SystemAccountDefinition definition, TransactionId transactionId) =>
        IdFor(definition.Code, ShardFor(definition, transactionId));
}
