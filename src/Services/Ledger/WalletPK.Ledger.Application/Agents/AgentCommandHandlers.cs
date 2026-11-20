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

namespace WalletPK.Ledger.Application.Agents;

/// <summary>
/// Agent commands from the Agent Network service (M9): open an agent wallet (its float), cash-in, cash-out,
/// move money between the agent's own wallet and its float, and pay accrued commissions. Each is safe to
/// repeat: a posted journal answers with its result again, a refusal with the same refusal.
/// </summary>
public sealed partial class AgentCommandHandlers(
    IAccountRepository accounts,
    IJournalRepository journals,
    ILedgerUnitOfWork unitOfWork,
    PostingService posting,
    ILimitsGateway limits,
    ITransferRejections rejections,
    IReplyOutbox replies,
    TimeProvider timeProvider,
    ILogger<AgentCommandHandlers> logger) :
    IIntegrationEventHandler<LedgerContracts.OpenAgentWallet>,
    IIntegrationEventHandler<LedgerContracts.AgentCashIn>,
    IIntegrationEventHandler<LedgerContracts.AgentCashOut>,
    IIntegrationEventHandler<LedgerContracts.MoveAgentFloat>,
    IIntegrationEventHandler<LedgerContracts.PayAgentCommission>
{
    public async Task HandleAsync(LedgerContracts.OpenAgentWallet command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owner = new UserId(command.OwnerUserId);
        var wallet = await accounts.GetWalletByOwnerAsync(owner, AccountType.AgentWallet, cancellationToken);
        if (wallet is null)
        {
            wallet = Account.OpenWallet(owner, AccountType.AgentWallet, command.Name.Length <= 100 ? command.Name : command.Name[..100], timeProvider.GetUtcNow(), command.AgentCode);
            accounts.Add(wallet);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        replies.Send(new LedgerContracts.AgentWalletOpened(command.AgentId, wallet.Id.Value, wallet.Alias ?? command.AgentCode), context);
    }

    public async Task HandleAsync(LedgerContracts.AgentCashIn command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var transactionId = new TransactionId(command.TransactionId);
        if (await AnsweredAsync(command.TransactionId, command.ReplyTo, context, cancellationToken))
        {
            return;
        }

        var agent = await accounts.GetWalletByOwnerAsync(new UserId(command.AgentOwnerUserId), AccountType.AgentWallet, cancellationToken);
        var customer = await accounts.GetWalletByAliasAsync(command.CustomerWalletNumber, cancellationToken);
        if (await journals.GetAsync(transactionId, cancellationToken) is { } done)
        {
            replies.Send(CashedIn(command, customer!, done.Entries.First(entry => entry.AccountId == agent!.Id).BalanceAfter, done.PostedAt), context);
            return;
        }

        if (command.AmountPaisa < 100 || command.CommissionPaisa < 0)
        {
            Reject(command.TransactionId, command.ReplyTo, "agent.amount_invalid", "The amount or commission is not valid.", context);
            return;
        }

        if (agent is null)
        {
            Reject(command.TransactionId, command.ReplyTo, "agent.no_float", "This agent has no float account.", context);
            return;
        }

        if (customer is null || customer.Type != AccountType.CustomerWallet)
        {
            Reject(command.TransactionId, command.ReplyTo, "wallet.recipient_not_found", "No WalletPK wallet has this number.", context);
            return;
        }

        if (customer.OwnerId == agent.OwnerId)
        {
            Reject(command.TransactionId, command.ReplyTo, "agent.own_wallet", "Use 'Move float' to put money in your own wallet.", context);
            return;
        }

        if (customer.Status == AccountStatus.Closed)
        {
            Reject(command.TransactionId, command.ReplyTo, "wallet.recipient_unavailable", "This wallet cannot receive money.", context);
            return;
        }

        var amount = Money.FromPaisa(command.AmountPaisa);
        List<PostingLine> lines =
        [
            PostingLine.Debit(AccountRef.Account(agent.Id), amount),
            PostingLine.Credit(AccountRef.Account(customer.Id), amount),
        ];
        Accrue(lines, command.CommissionPaisa);

        var posted = await posting.PostAsync(new PostJournalCommand(transactionId, JournalType.CashIn, Trim(command.Description), command.TransactionId.ToString(), lines), cancellationToken);
        if (posted.IsFailure)
        {
            var (code, reason) = FloatError(posted.Error, "Not enough float for this cash-in. Load your float first.");
            Reject(command.TransactionId, command.ReplyTo, code, reason, context);
            return;
        }

        replies.Send(CashedIn(command, customer, BalanceOf(posted.Value, agent.Id), posted.Value.PostedAt), context);
    }

    public async Task HandleAsync(LedgerContracts.AgentCashOut command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var transactionId = new TransactionId(command.TransactionId);
        if (await AnsweredAsync(command.TransactionId, command.ReplyTo, context, cancellationToken))
        {
            return;
        }

        var agent = await accounts.GetWalletByOwnerAsync(new UserId(command.AgentOwnerUserId), AccountType.AgentWallet, cancellationToken);
        if (await journals.GetAsync(transactionId, cancellationToken) is { } done)
        {
            replies.Send(CashedOut(command, done.Entries.First(entry => entry.AccountId == agent!.Id).BalanceAfter, done.PostedAt), context);
            return;
        }

        if (command.AmountPaisa < 100 || command.FeePaisa < 0 || command.TaxPaisa < 0 || command.CommissionPaisa < 0)
        {
            Reject(command.TransactionId, command.ReplyTo, "agent.amount_invalid", "The amount, fee or commission is not valid.", context);
            return;
        }

        var customer = await accounts.GetWalletByOwnerAsync(new UserId(command.CustomerUserId), AccountType.CustomerWallet, cancellationToken);
        if (agent is null || agent.Status == AccountStatus.Closed)
        {
            Reject(command.TransactionId, command.ReplyTo, "agent.no_float", "This agent cannot take cash-outs right now.", context);
            return;
        }

        if (customer is null)
        {
            Reject(command.TransactionId, command.ReplyTo, "wallet.not_found", "The customer has no wallet.", context);
            return;
        }

        if (customer.OwnerId == agent.OwnerId)
        {
            Reject(command.TransactionId, command.ReplyTo, "agent.own_wallet", "Use 'Move float' to take money from your own wallet.", context);
            return;
        }

        var total = Money.FromPaisa(command.AmountPaisa + command.FeePaisa + command.TaxPaisa);
        LimitDecisionResult decision;
        try
        {
            decision = await limits.ReserveAsync(transactionId, new UserId(command.CustomerUserId), JournalType.CashOut, total, command.DeviceId, cancellationToken);
        }
        catch (LimitsUnavailableException)
        {
            Reject(command.TransactionId, command.ReplyTo, "wallet.limits_unavailable", "Limits could not be checked. Try again in a moment.", context);
            return;
        }

        if (!decision.Allowed)
        {
            var error = WalletErrors.LimitExceeded(decision);
            Reject(command.TransactionId, command.ReplyTo, error.Code, error.Message, context);
            return;
        }

        List<PostingLine> lines =
        [
            PostingLine.Debit(AccountRef.Account(customer.Id), total),
            PostingLine.Credit(AccountRef.Account(agent.Id), Money.FromPaisa(command.AmountPaisa)),
        ];
        if (command.FeePaisa > 0)
        {
            lines.Add(PostingLine.Credit(AccountRef.System(ChartOfAccounts.FeeIncome), Money.FromPaisa(command.FeePaisa)));
        }

        if (command.TaxPaisa > 0)
        {
            lines.Add(PostingLine.Credit(AccountRef.System(ChartOfAccounts.TaxPayable), Money.FromPaisa(command.TaxPaisa)));
        }

        Accrue(lines, command.CommissionPaisa);

        var posted = await posting.PostAsync(new PostJournalCommand(transactionId, JournalType.CashOut, Trim(command.Description), command.TransactionId.ToString(), lines), cancellationToken);
        if (posted.IsFailure)
        {
            await limits.ReleaseAsync(transactionId, CancellationToken.None);
            var (code, reason) = posted.Error.Code == "ledger.insufficient_funds"
                ? ("wallet.insufficient_balance", "The customer does not have enough money for this cash-out and its fee.")
                : (posted.Error.Code, posted.Error.Message);
            Reject(command.TransactionId, command.ReplyTo, code, reason, context);
            return;
        }

        replies.Send(CashedOut(command, BalanceOf(posted.Value, agent.Id), posted.Value.PostedAt), context);
    }

    public async Task HandleAsync(LedgerContracts.MoveAgentFloat command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var transactionId = new TransactionId(command.TransferId);
        if (await AnsweredAsync(command.TransferId, command.ReplyTo, context, cancellationToken))
        {
            return;
        }

        var owner = new UserId(command.AgentOwnerUserId);
        var agent = await accounts.GetWalletByOwnerAsync(owner, AccountType.AgentWallet, cancellationToken);
        if (await journals.GetAsync(transactionId, cancellationToken) is { } done)
        {
            replies.Send(new LedgerContracts.AgentFloatMoved(command.TransferId, command.ReplyTo, command.Direction, command.AmountPaisa, done.Entries.First(entry => entry.AccountId == agent!.Id).BalanceAfter, done.PostedAt), context);
            return;
        }

        var wallet = await accounts.GetWalletByOwnerAsync(owner, AccountType.CustomerWallet, cancellationToken);
        var toFloat = command.Direction == LedgerContracts.MoveAgentFloat.ToFloat;
        if (command.AmountPaisa < 100 || (!toFloat && command.Direction != LedgerContracts.MoveAgentFloat.ToWallet))
        {
            Reject(command.TransferId, command.ReplyTo, "agent.amount_invalid", "The amount or direction is not valid.", context);
            return;
        }

        if (agent is null || wallet is null)
        {
            Reject(command.TransferId, command.ReplyTo, "agent.no_float", "Both your wallet and your float are needed.", context);
            return;
        }

        var amount = Money.FromPaisa(command.AmountPaisa);
        var (from, to) = toFloat ? (wallet, agent) : (agent, wallet);
        var posted = await posting.PostAsync(
            new PostJournalCommand(
                transactionId,
                JournalType.FloatTransfer,
                toFloat ? "Float loaded from my wallet" : "Float moved to my wallet",
                command.TransferId.ToString(),
                [PostingLine.Debit(AccountRef.Account(from.Id), amount), PostingLine.Credit(AccountRef.Account(to.Id), amount)]),
            cancellationToken);
        if (posted.IsFailure)
        {
            var (code, reason) = posted.Error.Code == "ledger.insufficient_funds"
                ? toFloat ? ("wallet.insufficient_balance", "Not enough money in your wallet.") : ("agent.float_insufficient", "Not enough float.")
                : (posted.Error.Code, posted.Error.Message);
            Reject(command.TransferId, command.ReplyTo, code, reason, context);
            return;
        }

        replies.Send(new LedgerContracts.AgentFloatMoved(command.TransferId, command.ReplyTo, command.Direction, command.AmountPaisa, BalanceOf(posted.Value, agent.Id), posted.Value.PostedAt), context);
    }

    public async Task HandleAsync(LedgerContracts.PayAgentCommission command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var transactionId = new TransactionId(command.PayoutId);
        if (await AnsweredAsync(command.PayoutId, command.AgentId, context, cancellationToken))
        {
            return;
        }

        var net = command.GrossPaisa - command.TaxPaisa;
        if (await journals.GetAsync(transactionId, cancellationToken) is { } done)
        {
            replies.Send(new LedgerContracts.AgentCommissionPaid(command.PayoutId, command.AgentId, command.GrossPaisa, command.TaxPaisa, net, done.PostedAt), context);
            return;
        }

        var agent = await accounts.GetWalletByOwnerAsync(new UserId(command.AgentOwnerUserId), AccountType.AgentWallet, cancellationToken);
        if (agent is null || command.GrossPaisa <= 0 || command.TaxPaisa < 0 || net <= 0)
        {
            Reject(command.PayoutId, command.AgentId, "agent.payout_invalid", "No agent float, or nothing to pay.", context);
            return;
        }

        List<PostingLine> lines =
        [
            PostingLine.Debit(AccountRef.System(ChartOfAccounts.CommissionPayable), Money.FromPaisa(command.GrossPaisa)),
            PostingLine.Credit(AccountRef.Account(agent.Id), Money.FromPaisa(net)),
        ];
        if (command.TaxPaisa > 0)
        {
            lines.Add(PostingLine.Credit(AccountRef.System(ChartOfAccounts.TaxPayable), Money.FromPaisa(command.TaxPaisa)));
        }

        var posted = await posting.PostAsync(new PostJournalCommand(transactionId, JournalType.CommissionPayout, Trim(command.Description), command.PayoutId.ToString(), lines), cancellationToken);
        if (posted.IsFailure)
        {
            Reject(command.PayoutId, command.AgentId, posted.Error.Code, posted.Error.Message, context);
            return;
        }

        replies.Send(new LedgerContracts.AgentCommissionPaid(command.PayoutId, command.AgentId, command.GrossPaisa, command.TaxPaisa, net, posted.Value.PostedAt), context);
    }

    // ---- Helpers ----

    /// <summary>Answers a command refused before with the same refusal; true when it did.</summary>
    private async Task<bool> AnsweredAsync(Guid id, Guid replyTo, EventContext context, CancellationToken cancellationToken)
    {
        if (await rejections.FindAsync(id, cancellationToken) is not { } refused)
        {
            return false;
        }

        replies.Send(new LedgerContracts.FundsTransferRejected(id, replyTo, refused.Code, refused.Reason), context);
        return true;
    }

    /// <summary>The agent's commission, accrued in the same journal: an expense now, paid to the float later.</summary>
    private static void Accrue(List<PostingLine> lines, long commissionPaisa)
    {
        if (commissionPaisa <= 0)
        {
            return;
        }

        var commission = Money.FromPaisa(commissionPaisa);
        lines.Add(PostingLine.Debit(AccountRef.System(ChartOfAccounts.CommissionExpense), commission));
        lines.Add(PostingLine.Credit(AccountRef.System(ChartOfAccounts.CommissionPayable), commission));
    }

    private static (string Code, string Reason) FloatError(WalletPK.Common.Results.Error error, string insufficient) =>
        error.Code == "ledger.insufficient_funds" ? ("agent.float_insufficient", insufficient) : (error.Code, error.Message);

    private static long BalanceOf(PostedJournal journal, AccountId accountId) =>
        journal.Entries.First(entry => entry.AccountId == accountId).BalanceAfter.MinorUnits;

    private static LedgerContracts.AgentCashedIn CashedIn(LedgerContracts.AgentCashIn command, Account customer, long agentBalance, DateTimeOffset postedAt) =>
        new(command.TransactionId, command.ReplyTo, customer.OwnerId?.Value ?? Guid.Empty, customer.Name, command.AmountPaisa, command.CommissionPaisa, agentBalance, postedAt);

    private static LedgerContracts.AgentCashedOut CashedOut(LedgerContracts.AgentCashOut command, long agentBalance, DateTimeOffset postedAt) =>
        new(command.TransactionId, command.ReplyTo, command.AmountPaisa, command.FeePaisa, command.TaxPaisa, command.CommissionPaisa, agentBalance, postedAt);

    private void Reject(Guid id, Guid replyTo, string code, string reason, EventContext context)
    {
        LogRejected(logger, id, code);
        rejections.Add(new TransferRejection(id, code, reason, timeProvider.GetUtcNow()));
        replies.Send(new LedgerContracts.FundsTransferRejected(id, replyTo, code, reason), context);
    }

    private static string Trim(string description) => description.Length <= 200 ? description : description[..200];

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent command {Id} refused: {Code}")]
    private static partial void LogRejected(ILogger logger, Guid id, string code);
}
