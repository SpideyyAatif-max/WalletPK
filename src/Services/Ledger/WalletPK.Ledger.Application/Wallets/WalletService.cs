using System.Security.Cryptography;
using System.Text;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Fees;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Application.ReadModels;
using WalletPK.Ledger.Application.Reports;
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

    public static Error StatementMonthInvalid { get; } =
        Error.Validation("wallet.statement_month_invalid", "Ask for a month that has started, as yyyy-MM (for example 2026-11).");

    public static Error StatementTooLong { get; } =
        Error.BusinessRule("wallet.statement_too_long", "This month has too many entries for one statement; download the history instead.");

    public static Error StatementFilterInvalid(string detail) =>
        Error.Validation("wallet.statement_filter_invalid", detail);

    public static Error LimitsUnavailable { get; } =
        Error.Unavailable("wallet.limits_unavailable", "Payments are temporarily unavailable. Please try again in a minute.");

    /// <summary>Refused by the Identity service's limits; the reason is safe to show to the customer.</summary>
    public static Error LimitExceeded(LimitDecisionResult decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Decision switch
        {
            RiskDecisions.StepUp => Error.BusinessRule("fraud.step_up_required", decision.Reason),
            RiskDecisions.Review => Error.BusinessRule("fraud.review", decision.Reason),
            RiskDecisions.Blocked => Error.BusinessRule("fraud.blocked", decision.Reason),
            _ => Error.BusinessRule("wallet.limit_exceeded", decision.Reason),
        };
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
    IReadModelQueries readModels,
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

    public Task<Result<WalletView>> GetAsync(UserId ownerId, CancellationToken cancellationToken) =>
        GetAsync(ownerId, AccountType.CustomerWallet, cancellationToken);

    /// <summary>The owner's wallet of <paramref name="type"/>: their customer wallet, merchant wallet (M8) or agent float (M9).</summary>
    public async Task<Result<WalletView>> GetAsync(UserId ownerId, AccountType type, CancellationToken cancellationToken) =>
        await accounts.GetWalletByOwnerAsync(ownerId, type, cancellationToken) is { } wallet
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
            decision = await limits.ReserveAsync(command.TransactionId, command.SenderId, JournalType.P2PTransfer, command.Amount, command.DeviceId, recipientNumber, cancellationToken);
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

    /// <summary>
    /// The wallet's statement, newest first, in pages, optionally filtered by type, direction and date.
    /// Served from the history read model (M4.4), so a payment shows up a few milliseconds after it posts.
    /// </summary>
    public async Task<Result<StatementPage>> HistoryAsync(UserId ownerId, StatementQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        JournalType? type = null;
        if (query.Type is { Length: > 0 } typeName)
        {
            if (!Enum.TryParse<JournalType>(typeName, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(typeName, out _))
            {
                return WalletErrors.StatementFilterInvalid($"Unknown type '{typeName}'. Use one of: {string.Join(", ", Enum.GetNames<JournalType>())}.");
            }

            type = parsed;
        }

        EntryDirection? direction;
        switch (query.Direction?.Trim().ToUpperInvariant())
        {
            case null or "":
                direction = null;
                break;
            case "IN":
                direction = EntryDirection.Credit;
                break;
            case "OUT":
                direction = EntryDirection.Debit;
                break;
            default:
                return WalletErrors.StatementFilterInvalid("Direction must be \"in\" or \"out\".");
        }

        if (query.From is { } from && query.To is { } to && from >= to)
        {
            return WalletErrors.StatementFilterInvalid("\"from\" must be before \"to\".");
        }

        var walletId = await CustomerWalletIdAsync(ownerId, cancellationToken);
        if (walletId is null)
        {
            return WalletErrors.WalletNotFound;
        }

        var size = Math.Clamp(query.Limit ?? 20, 1, MaxPageSize);

        // One extra row tells whether there is another page, without a COUNT query.
        var rows = await readModels.GetHistoryAsync(
            new HistoryQuery(walletId.Value, size + 1, query.Before, type, direction, query.From, query.To),
            cancellationToken);
        var hasMore = rows.Count > size;

        StatementLine[] items =
        [
            .. rows.Take(size).Select(row => new StatementLine(
                row.Sequence,
                row.TransactionId,
                row.Type,
                StatementTitles.For(row.Type, row.Description, row.Direction, row.CounterpartyWalletNumber),
                row.Direction == EntryDirection.Debit ? "Out" : "In",
                row.Amount,
                row.BalanceAfter,
                row.PostedAt)),
        ];
        return new StatementPage(items, hasMore ? items[^1].Sequence : null);
    }

    /// <summary>
    /// The wallet's balance from the balance read model, with the sequence of the newest entry it
    /// includes, so a live view can tell whether a pushed update is newer. For the authoritative balance
    /// with holds, use <see cref="GetAsync(UserId, CancellationToken)"/>.
    /// </summary>
    public async Task<Result<WalletBalance>> BalanceAsync(UserId ownerId, CancellationToken cancellationToken)
    {
        if (await readModels.GetWalletAsync(ownerId, AccountType.CustomerWallet, cancellationToken) is { } row)
        {
            return new WalletBalance(row.WalletId, row.WalletNumber, row.Balance, row.Sequence, row.UpdatedAt);
        }

        // Not projected yet (opened a moment ago): the ledger itself answers.
        return await accounts.GetWalletByOwnerAsync(ownerId, AccountType.CustomerWallet, cancellationToken) is { } wallet
            ? new WalletBalance(wallet.Id, wallet.Alias, Money.FromPaisa(wallet.Balance), wallet.EntrySequence, wallet.UpdatedAt)
            : WalletErrors.WalletNotFound;
    }

    /// <summary>The most entries one statement holds (a month of a very busy wallet).</summary>
    public const int MaxStatementEntries = 20_000;

    /// <summary>
    /// The customer's statement for a calendar month, Pakistan time (M12.5): the opening balance, every entry
    /// oldest first, and the closing balance. The month in progress is "to date".
    /// </summary>
    public async Task<Result<MonthlyStatement>> StatementAsync(UserId ownerId, string? month, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (StatementMonth.Parse(month) is not { } period || period.Start > now || period.Year < 2000)
        {
            return WalletErrors.StatementMonthInvalid;
        }

        var wallet = await readModels.GetWalletAsync(ownerId, AccountType.CustomerWallet, cancellationToken);
        var walletId = wallet?.WalletId ?? (await accounts.GetWalletByOwnerAsync(ownerId, AccountType.CustomerWallet, cancellationToken))?.Id;
        if (walletId is null)
        {
            return WalletErrors.WalletNotFound;
        }

        var to = period.End < now ? period.End : now;
        var before = await readModels.GetHistoryAsync(new HistoryQuery(walletId.Value, 1, To: period.Start), cancellationToken);
        var opening = before.Count > 0 ? before[0].BalanceAfter : Money.Zero();
        // Up to the month's end even when it is in progress: an entry posted this very instant belongs in it.
        var rows = await readModels.GetHistoryAsync(new HistoryQuery(walletId.Value, MaxStatementEntries + 1, From: period.Start, To: period.End), cancellationToken);
        if (rows.Count > MaxStatementEntries)
        {
            return WalletErrors.StatementTooLong;
        }

        StatementEntry[] entries =
        [
            .. rows.OrderBy(row => row.Sequence).Select(row => new StatementEntry(
                row.PostedAt,
                StatementTitles.For(row.Type, row.Description, row.Direction, row.CounterpartyWalletNumber),
                row.Type,
                row.TransactionId.Value,
                row.Direction == EntryDirection.Credit ? row.Amount : null,
                row.Direction == EntryDirection.Debit ? row.Amount : null,
                row.BalanceAfter)),
        ];
        var moneyIn = Money.FromPaisa(entries.Sum(entry => entry.Credit?.MinorUnits ?? 0));
        var moneyOut = Money.FromPaisa(entries.Sum(entry => entry.Debit?.MinorUnits ?? 0));
        var closing = entries.Length > 0 ? entries[^1].BalanceAfter : opening;
        return new MonthlyStatement(wallet?.WalletNumber, period, period.Start, to, opening, closing, moneyIn, moneyOut, entries, now, period.End <= now);
    }

    private async Task<AccountId?> CustomerWalletIdAsync(UserId ownerId, CancellationToken cancellationToken) =>
        (await readModels.GetWalletAsync(ownerId, AccountType.CustomerWallet, cancellationToken))?.WalletId
        ?? (await accounts.GetWalletByOwnerAsync(ownerId, AccountType.CustomerWallet, cancellationToken))?.Id;

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
