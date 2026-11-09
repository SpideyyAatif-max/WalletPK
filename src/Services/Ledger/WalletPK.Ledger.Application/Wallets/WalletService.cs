using System.Security.Cryptography;
using System.Text;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Fees;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Wallets;

public static class WalletErrors
{
    public static Error WalletNotFound { get; } =
        Error.NotFound("wallet.not_found", "You do not have a wallet yet.");

    public static Error RecipientNotFound { get; } =
        Error.NotFound("wallet.recipient_not_found", "No WalletPK wallet uses this mobile number.");

    public static Error RecipientInvalid { get; } =
        Error.Validation("wallet.recipient_invalid", "Enter the recipient's Pakistani mobile number, for example 0300 1234567.");

    public static Error SelfTransfer { get; } =
        Error.Validation("wallet.self_transfer", "You cannot send money to your own wallet.");

    public static Error AmountInvalid { get; } =
        Error.Validation("wallet.amount_invalid", "Send at least Rs. 1.");

    public static Error PhoneNumberRequired { get; } =
        Error.Validation("wallet.phone_number_required", "Your sign-in does not include a mobile number. Please log in again.");

    public static Error RecipientUnavailable { get; } =
        Error.BusinessRule("wallet.recipient_unavailable", "This wallet cannot receive money right now.");

    public static Error LimitsUnavailable { get; } =
        Error.Unavailable("wallet.limits_unavailable", "Payments are temporarily unavailable. Please try again in a minute.");

    /// <summary>Refused by the Identity service's limits; the reason is safe to show to the customer.</summary>
    public static Error LimitExceeded(LimitDecisionResult decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return Error.BusinessRule("wallet.limit_exceeded", decision.Reason);
    }
}

/// <summary>
/// Customer wallets (M3.8): open, read, send money (P2P) and statement history.
/// A P2P transfer is: fee quote (M3.9) → limit reservation at Identity over gRPC (M2.6) → one balanced
/// journal (sender pays amount + fee + tax; recipient, FEE_INCOME and TAX_PAYABLE are credited) under
/// wallet locks (M3.6). If the journal fails, the limit reservation is released.
/// </summary>
public sealed class WalletService(
    IAccountRepository accounts,
    IJournalRepository journals,
    ILedgerUnitOfWork unitOfWork,
    ILedgerQueries queries,
    PostingService posting,
    FeeEngine fees,
    ILimitsGateway limits,
    TimeProvider timeProvider)
{
    public const int MaxPageSize = 100;

    /// <summary>
    /// The transaction ID for a client request: a name-based UUID (version 8) from the user and their
    /// Idempotency-Key. A retry that slips past the HTTP idempotency layer (for example after its record
    /// expired) still maps to the same journal, which the database posts only once.
    /// </summary>
    public static TransactionId TransactionIdFor(UserId userId, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"walletpk:ledger:p2p:{userId}:{idempotencyKey}"), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8 (custom)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return TransactionId.From(new Guid(bytes, bigEndian: true));
    }

    /// <summary>
    /// Opens the user's customer wallet, numbered with their mobile number. Idempotent: a second call
    /// returns the existing wallet. (In M4 this also runs automatically on identity.user_registered.)
    /// </summary>
    public async Task<Result<OpenWalletResult>> OpenAsync(UserId ownerId, string? phoneNumber, CancellationToken cancellationToken)
    {
        if (await accounts.GetWalletByOwnerAsync(ownerId, AccountType.CustomerWallet, cancellationToken) is { } existing)
        {
            return new OpenWalletResult(WalletView.From(existing), Created: false);
        }

        if (!PakistaniMobileNumber.TryNormalize(phoneNumber, out var walletNumber))
        {
            return WalletErrors.PhoneNumberRequired;
        }

        var wallet = Account.OpenWallet(ownerId, AccountType.CustomerWallet, PakistaniMobileNumber.Mask(walletNumber), timeProvider.GetUtcNow(), walletNumber);
        accounts.Add(wallet);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateKeyException)
        {
            // Opened by a parallel request (or by the M4 event consumer) a moment ago.
            return await accounts.GetWalletByOwnerAsync(ownerId, AccountType.CustomerWallet, cancellationToken) is { } winner
                ? new OpenWalletResult(WalletView.From(winner), Created: false)
                : WalletErrors.WalletNotFound;
        }

        return new OpenWalletResult(WalletView.From(wallet), Created: true);
    }

    public async Task<Result<WalletView>> GetAsync(UserId ownerId, CancellationToken cancellationToken) =>
        await accounts.GetWalletByOwnerAsync(ownerId, AccountType.CustomerWallet, cancellationToken) is { } wallet
            ? WalletView.From(wallet)
            : WalletErrors.WalletNotFound;

    /// <summary>What a P2P transfer of <paramref name="amount"/> would cost.</summary>
    public Result<FeeQuote> QuoteTransfer(Money amount) =>
        amount < Money.FromRupees(1) ? WalletErrors.AmountInvalid : fees.Quote(JournalType.P2PTransfer, amount);

    public async Task<Result<TransferReceipt>> TransferAsync(TransferCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Amount < Money.FromRupees(1) || command.Amount.Currency.Code != Currency.Pkr.Code)
        {
            return WalletErrors.AmountInvalid;
        }

        if (!PakistaniMobileNumber.TryNormalize(command.ToPhoneNumber, out var recipientNumber))
        {
            return WalletErrors.RecipientInvalid;
        }

        var sender = await accounts.GetWalletByOwnerAsync(command.SenderId, AccountType.CustomerWallet, cancellationToken);
        if (sender is null)
        {
            return WalletErrors.WalletNotFound;
        }

        // A retry of a transfer that already went through (same Idempotency-Key) returns its receipt.
        if (await journals.GetAsync(command.TransactionId, cancellationToken) is { } done)
        {
            return Receipt(PostedJournal.From(done, alreadyPosted: true), sender.Id, recipientNumber);
        }

        var recipient = await accounts.GetWalletByAliasAsync(recipientNumber, cancellationToken);
        if (recipient is null)
        {
            return WalletErrors.RecipientNotFound;
        }

        if (recipient.Id == sender.Id)
        {
            return WalletErrors.SelfTransfer;
        }

        if (recipient.Status == AccountStatus.Closed)
        {
            return WalletErrors.RecipientUnavailable;
        }

        var quote = fees.Quote(JournalType.P2PTransfer, command.Amount);

        LimitDecisionResult decision;
        try
        {
            decision = await limits.ReserveAsync(command.TransactionId, command.SenderId, JournalType.P2PTransfer, command.Amount, command.DeviceId, cancellationToken);
        }
        catch (LimitsUnavailableException)
        {
            return WalletErrors.LimitsUnavailable;
        }

        if (!decision.Allowed)
        {
            return WalletErrors.LimitExceeded(decision);
        }

        List<PostingLine> lines =
        [
            PostingLine.Debit(AccountRef.Account(sender.Id), quote.TotalDebit),
            PostingLine.Credit(AccountRef.Account(recipient.Id), quote.Amount),
        ];
        if (quote.Fee.IsPositive)
        {
            lines.Add(PostingLine.Credit(AccountRef.System(ChartOfAccounts.FeeIncome), quote.Fee));
        }

        if (quote.Tax.IsPositive)
        {
            lines.Add(PostingLine.Credit(AccountRef.System(ChartOfAccounts.TaxPayable), quote.Tax));
        }

        var posted = await posting.PostAsync(
            new PostJournalCommand(command.TransactionId, JournalType.P2PTransfer, "Wallet to wallet transfer", null, lines),
            cancellationToken);

        if (posted.IsFailure)
        {
            // The money did not move, so give the limit back.
            await limits.ReleaseAsync(command.TransactionId, CancellationToken.None);
            return posted.Error;
        }

        return Receipt(posted.Value, sender.Id, recipientNumber);
    }

    /// <summary>The wallet's statement, newest first, in pages.</summary>
    public async Task<Result<StatementPage>> HistoryAsync(UserId ownerId, int? limit, long? before, CancellationToken cancellationToken)
    {
        var wallet = await accounts.GetWalletByOwnerAsync(ownerId, AccountType.CustomerWallet, cancellationToken);
        if (wallet is null)
        {
            return WalletErrors.WalletNotFound;
        }

        var size = Math.Clamp(limit ?? 20, 1, MaxPageSize);

        // One extra row tells whether there is another page, without a COUNT query.
        var lines = await queries.GetHistoryAsync(wallet.Id, size + 1, before, cancellationToken);
        var hasMore = lines.Count > size;

        StatementLine[] items =
        [
            .. lines.Take(size).Select(line => new StatementLine(
                line.EntryId,
                line.TransactionId,
                line.Type,
                Title(line),
                line.Direction == EntryDirection.Debit ? "Out" : "In",
                line.Amount,
                line.BalanceAfter,
                line.PostedAt)),
        ];
        return new StatementPage(items, hasMore ? items[^1].EntryId : null);
    }

    private static string Title(HistoryLine line) => (line.Type, line.CounterpartyNumber, line.Direction) switch
    {
        (JournalType.P2PTransfer, { } other, EntryDirection.Debit) => $"Sent to {PakistaniMobileNumber.Mask(other)}",
        (JournalType.P2PTransfer, { } other, EntryDirection.Credit) => $"Received from {PakistaniMobileNumber.Mask(other)}",
        (JournalType.CashIn, _, _) => "Cash-in",
        (JournalType.CashOut, _, _) => "Cash-out",
        _ => line.Description,
    };

    private static Result<TransferReceipt> Receipt(PostedJournal journal, AccountId senderId, string recipientNumber)
    {
        var feeAccount = ChartOfAccounts.ShardIdFor(ChartOfAccounts.FeeIncome, journal.TransactionId);
        var taxAccount = ChartOfAccounts.ShardIdFor(ChartOfAccounts.TaxPayable, journal.TransactionId);

        var senderEntry = journal.Entries.FirstOrDefault(entry => entry.AccountId == senderId && entry.Direction == EntryDirection.Debit);
        var recipientEntry = journal.Entries.FirstOrDefault(entry =>
            entry.Direction == EntryDirection.Credit && entry.AccountId != feeAccount && entry.AccountId != taxAccount);

        if (journal.Type != JournalType.P2PTransfer || senderEntry is null || recipientEntry is null)
        {
            // The transaction ID is derived from the sender and the Idempotency-Key, so this means the key
            // was reused for something else.
            return Error.Conflict("wallet.transaction_mismatch", "This Idempotency-Key was already used for a different request.");
        }

        Money Sum(AccountId account) => journal.Entries
            .Where(entry => entry.AccountId == account && entry.Direction == EntryDirection.Credit)
            .Aggregate(Money.Zero(), (sum, entry) => sum + entry.Amount);

        return new TransferReceipt(
            journal.TransactionId,
            PakistaniMobileNumber.Mask(recipientNumber),
            recipientEntry.Amount,
            Sum(feeAccount),
            Sum(taxAccount),
            senderEntry.Amount,
            senderEntry.BalanceAfter,
            journal.PostedAt);
    }
}
