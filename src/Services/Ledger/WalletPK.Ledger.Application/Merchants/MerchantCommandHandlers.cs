using Microsoft.Extensions.Logging;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Holds;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Messaging.Abstractions;
using LedgerContracts = WalletPK.Contracts.Ledger;

namespace WalletPK.Ledger.Application.Merchants;

/// <summary>
/// Merchant commands from the Top-up &amp; Merchant service (M8): open a merchant wallet, take a QR payment
/// with the merchant's fee split off in the same journal, and sweep a day's sales to the bank. Each is
/// safe to repeat: a posted journal answers with its result again, a refusal with the same refusal.
/// </summary>
public sealed partial class MerchantCommandHandlers(
    IAccountRepository accounts,
    IJournalRepository journals,
    ILedgerUnitOfWork unitOfWork,
    PostingService posting,
    ILimitsGateway limits,
    ITransferRejections rejections,
    IReplyOutbox replies,
    TimeProvider timeProvider,
    ILogger<MerchantCommandHandlers> logger) :
    IIntegrationEventHandler<LedgerContracts.OpenMerchantWallet>,
    IIntegrationEventHandler<LedgerContracts.PayMerchant>,
    IIntegrationEventHandler<LedgerContracts.SettleMerchant>
{
    public async Task HandleAsync(LedgerContracts.OpenMerchantWallet command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owner = new UserId(command.OwnerUserId);
        var wallet = await accounts.GetWalletByOwnerAsync(owner, AccountType.MerchantWallet, cancellationToken);
        if (wallet is null)
        {
            wallet = Account.OpenWallet(owner, AccountType.MerchantWallet, command.Name.Length <= 100 ? command.Name : command.Name[..100], timeProvider.GetUtcNow(), command.MerchantCode);
            accounts.Add(wallet);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        replies.Send(new LedgerContracts.MerchantWalletOpened(command.MerchantId, wallet.Id.Value, wallet.Alias ?? command.MerchantCode), context);
    }

    public async Task HandleAsync(LedgerContracts.PayMerchant command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var transactionId = new TransactionId(command.PaymentId);

        if (await rejections.FindAsync(command.PaymentId, cancellationToken) is { } refused)
        {
            replies.Send(new LedgerContracts.FundsTransferRejected(command.PaymentId, command.ReplyTo, refused.Code, refused.Reason), context);
            return;
        }

        if (await journals.GetAsync(transactionId, cancellationToken) is not null)
        {
            replies.Send(Paid(command, timeProvider.GetUtcNow()), context);
            return;
        }

        var net = command.AmountPaisa - command.FeePaisa - command.TaxPaisa;
        if (command.AmountPaisa < 100 || command.FeePaisa < 0 || command.TaxPaisa < 0 || net <= 0)
        {
            Reject(command.PaymentId, command.ReplyTo, "merchant.amount_invalid", "The amount, fee and tax do not add up.", context);
            return;
        }

        var payer = await accounts.GetWalletByOwnerAsync(new UserId(command.PayerUserId), AccountType.CustomerWallet, cancellationToken);
        var merchant = await accounts.GetWalletByOwnerAsync(new UserId(command.MerchantOwnerUserId), AccountType.MerchantWallet, cancellationToken);
        if (payer is null)
        {
            Reject(command.PaymentId, command.ReplyTo, "wallet.not_found", "You do not have a wallet yet.", context);
            return;
        }

        if (merchant is null || merchant.Status == AccountStatus.Closed)
        {
            Reject(command.PaymentId, command.ReplyTo, "merchant.unavailable", "This merchant cannot receive payments right now.", context);
            return;
        }

        var amount = Money.FromPaisa(command.AmountPaisa);
        LimitDecisionResult decision;
        try
        {
            decision = await limits.ReserveAsync(transactionId, new UserId(command.PayerUserId), JournalType.MerchantPayment, amount, command.DeviceId, cancellationToken);
        }
        catch (LimitsUnavailableException)
        {
            Reject(command.PaymentId, command.ReplyTo, "wallet.limits_unavailable", "Limits could not be checked. Try again in a moment.", context);
            return;
        }

        if (!decision.Allowed)
        {
            var error = WalletErrors.LimitExceeded(decision);
            Reject(command.PaymentId, command.ReplyTo, error.Code, error.Message, context);
            return;
        }

        List<PostingLine> lines =
        [
            PostingLine.Debit(AccountRef.Account(payer.Id), amount),
            PostingLine.Credit(AccountRef.Account(merchant.Id), Money.FromPaisa(net)),
        ];
        if (command.FeePaisa > 0)
        {
            lines.Add(PostingLine.Credit(AccountRef.System(ChartOfAccounts.FeeIncome), Money.FromPaisa(command.FeePaisa)));
        }

        if (command.TaxPaisa > 0)
        {
            lines.Add(PostingLine.Credit(AccountRef.System(ChartOfAccounts.TaxPayable), Money.FromPaisa(command.TaxPaisa)));
        }

        var posted = await posting.PostAsync(
            new PostJournalCommand(transactionId, JournalType.MerchantPayment, Trim(command.Description), command.PaymentId.ToString(), lines),
            cancellationToken);
        if (posted.IsFailure)
        {
            await limits.ReleaseAsync(transactionId, CancellationToken.None);
            Reject(command.PaymentId, command.ReplyTo, posted.Error.Code, posted.Error.Message, context);
            return;
        }

        replies.Send(Paid(command, posted.Value.PostedAt), context);
    }

    public async Task HandleAsync(LedgerContracts.SettleMerchant command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var transactionId = new TransactionId(command.SettlementId);

        if (await rejections.FindAsync(command.SettlementId, cancellationToken) is { } refused)
        {
            replies.Send(new LedgerContracts.FundsTransferRejected(command.SettlementId, command.MerchantId, refused.Code, refused.Reason), context);
            return;
        }

        if (await journals.GetAsync(transactionId, cancellationToken) is { } done)
        {
            replies.Send(new LedgerContracts.MerchantSettled(command.SettlementId, command.MerchantId, command.AmountPaisa, done.PostedAt), context);
            return;
        }

        var merchant = await accounts.GetWalletByOwnerAsync(new UserId(command.MerchantOwnerUserId), AccountType.MerchantWallet, cancellationToken);
        if (merchant is null || command.AmountPaisa <= 0)
        {
            Reject(command.SettlementId, command.MerchantId, "merchant.settlement_invalid", "No merchant wallet, or nothing to settle.", context);
            return;
        }

        var amount = Money.FromPaisa(command.AmountPaisa);
        var posted = await posting.PostAsync(
            new PostJournalCommand(
                transactionId,
                JournalType.CashOut,
                Trim(command.Description),
                command.SettlementId.ToString(),
                [PostingLine.Debit(AccountRef.Account(merchant.Id), amount), PostingLine.Credit(AccountRef.System(ChartOfAccounts.BankFloat), amount)]),
            cancellationToken);
        if (posted.IsFailure)
        {
            Reject(command.SettlementId, command.MerchantId, posted.Error.Code, posted.Error.Message, context);
            return;
        }

        replies.Send(new LedgerContracts.MerchantSettled(command.SettlementId, command.MerchantId, command.AmountPaisa, posted.Value.PostedAt), context);
    }

    private static LedgerContracts.MerchantPaid Paid(LedgerContracts.PayMerchant command, DateTimeOffset postedAt) =>
        new(command.PaymentId, command.ReplyTo, command.AmountPaisa, command.FeePaisa, command.TaxPaisa, command.AmountPaisa - command.FeePaisa - command.TaxPaisa, postedAt);

    private void Reject(Guid id, Guid replyTo, string code, string reason, EventContext context)
    {
        LogRejected(logger, id, code);
        rejections.Add(new TransferRejection(id, code, reason, timeProvider.GetUtcNow()));
        replies.Send(new LedgerContracts.FundsTransferRejected(id, replyTo, code, reason), context);
    }

    private static string Trim(string description) => description.Length <= 200 ? description : description[..200];

    [LoggerMessage(Level = LogLevel.Information, Message = "Merchant command {Id} refused: {Code}")]
    private static partial void LogRejected(ILogger logger, Guid id, string code);
}
