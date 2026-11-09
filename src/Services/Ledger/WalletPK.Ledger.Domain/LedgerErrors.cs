using WalletPK.Common.Results;
using WalletPK.Ledger.Domain.Accounts;

namespace WalletPK.Ledger.Domain;

public static class LedgerErrors
{
    public static Error TooFewLines { get; } =
        Error.Validation("ledger.too_few_lines", "A journal needs at least one debit and one credit.");

    public static Error TooManyLines { get; } =
        Error.Validation("ledger.too_many_lines", $"A journal can have at most {Journals.JournalTransaction.MaxLines} lines.");

    public static Error AmountInvalid { get; } =
        Error.Validation("ledger.amount_invalid", "Every journal line needs a positive amount.");

    public static Error Unbalanced { get; } =
        Error.Validation("ledger.unbalanced", "Total debits must equal total credits.");

    public static Error DescriptionRequired { get; } =
        Error.Validation("ledger.description_required", "A journal needs a description.");

    public static Error CurrencyMismatch { get; } =
        Error.Validation("ledger.currency_mismatch", "All accounts in a journal must use the same currency.");

    public static Error InsufficientFunds(AccountId accountId) =>
        new("ledger.insufficient_funds", "Insufficient balance.", ErrorType.BusinessRule,
            new Dictionary<string, string[]> { ["accountId"] = [accountId.ToString()] });

    public static Error AccountFrozen(AccountId accountId) =>
        new("ledger.account_frozen", "This account is on hold; money cannot leave it.", ErrorType.BusinessRule,
            new Dictionary<string, string[]> { ["accountId"] = [accountId.ToString()] });

    public static Error AccountClosed(AccountId accountId) =>
        new("ledger.account_closed", "This account is closed.", ErrorType.BusinessRule,
            new Dictionary<string, string[]> { ["accountId"] = [accountId.ToString()] });

    public static Error LockLost(AccountId accountId) =>
        new("ledger.lock_lost", "The wallet lock expired before the payment was saved. Please try again.", ErrorType.Conflict,
            new Dictionary<string, string[]> { ["accountId"] = [accountId.ToString()] });

    public static Error HoldNotFound { get; } =
        Error.NotFound("ledger.hold_not_found", "Hold not found.");

    public static Error HoldNotActive { get; } =
        Error.Conflict("ledger.hold_not_active", "This hold has already been captured, released or expired.");

    public static Error HoldExpired { get; } =
        Error.BusinessRule("ledger.hold_expired", "This hold has expired; the money was returned to the wallet.");

    public static Error CaptureTooLarge { get; } =
        Error.BusinessRule("ledger.capture_exceeds_hold", "You cannot capture more than was held.");

    public static Error HoldLifetimeInvalid { get; } =
        Error.Validation("ledger.hold_lifetime_invalid", "A hold must last between 1 minute and 7 days.");

    public static Error AccountNotFound(AccountId accountId) =>
        new("ledger.account_not_found", "Account not found.", ErrorType.NotFound,
            new Dictionary<string, string[]> { ["accountId"] = [accountId.ToString()] });
}
