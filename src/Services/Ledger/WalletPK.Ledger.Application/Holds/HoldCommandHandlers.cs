using Microsoft.Extensions.Logging;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Messaging.Abstractions;
using LedgerContracts = WalletPK.Contracts.Ledger;

namespace WalletPK.Ledger.Application.Holds;

/// <summary>Queues a reply in the Ledger's outbox: sent if, and only if, the handler's transaction commits.</summary>
public interface IReplyOutbox
{
    void Send(Messaging.Events.IntegrationEvent reply, EventContext answering);
}

/// <summary>
/// Answers hold commands from Sagas (M6), arriving on "ledger.commands" through the inbox. Each command
/// is handled once (the inbox), each answer leaves in the same transaction as the change (the outbox), and
/// every command is safe to repeat: the hold ID is the caller's, a capture's journal ID is the caller's,
/// and releasing a returned hold just says so again. A customer debit counts against the Identity
/// service's limits when its hold is placed (reserved under the hold ID) and is given back on release.
/// </summary>
public sealed partial class HoldCommandHandlers(
    HoldService holds,
    IAccountRepository accounts,
    ILimitsGateway limits,
    IReplyOutbox replies,
    ILogger<HoldCommandHandlers> logger) :
    IIntegrationEventHandler<LedgerContracts.PlaceHold>,
    IIntegrationEventHandler<LedgerContracts.CaptureHold>,
    IIntegrationEventHandler<LedgerContracts.ReleaseHold>
{
    /// <summary>Hold purposes that are customer payments, so they count against limits.</summary>
    private static readonly HashSet<JournalType> _limited =
        [JournalType.BankTransfer, JournalType.BillPayment, JournalType.MobileTopup, JournalType.MerchantPayment, JournalType.P2PTransfer, JournalType.CashOut];

    public async Task HandleAsync(LedgerContracts.PlaceHold command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        void Reject(string code, string reason)
        {
            LogRejected(logger, command.HoldId, code);
            replies.Send(new LedgerContracts.HoldRejected(command.HoldId, code, reason), context);
        }

        if (!Enum.TryParse<JournalType>(command.Purpose, out var purpose) || !Enum.IsDefined(purpose))
        {
            Reject("ledger.invalid_purpose", $"Unknown purpose '{command.Purpose}'.");
            return;
        }

        if (command.AmountPaisa <= 0)
        {
            Reject(LedgerErrors.AmountInvalid.Code, LedgerErrors.AmountInvalid.Message);
            return;
        }

        var wallet = await accounts.GetWalletByOwnerAsync(new UserId(command.UserId), AccountType.CustomerWallet, cancellationToken);
        if (wallet is null)
        {
            Reject(WalletErrors.WalletNotFound.Code, WalletErrors.WalletNotFound.Message);
            return;
        }

        var holdId = new HoldId(command.HoldId);
        var limitKey = new TransactionId(command.HoldId);
        var amount = Money.FromPaisa(command.AmountPaisa);

        // A repeated command for a hold that exists: answer again, without counting it against limits twice.
        if (await holds.FindAsync(holdId, cancellationToken) is { } earlier)
        {
            replies.Send(new LedgerContracts.HoldPlaced(command.HoldId, earlier.AccountId.Value, earlier.Amount, earlier.ExpiresAt), context);
            return;
        }

        var limited = _limited.Contains(purpose);
        if (limited)
        {
            LimitDecisionResult decision;
            try
            {
                decision = await limits.ReserveAsync(limitKey, new UserId(command.UserId), purpose, amount, command.DeviceId, cancellationToken);
            }
            catch (LimitsUnavailableException)
            {
                Reject(WalletErrors.LimitsUnavailable.Code, WalletErrors.LimitsUnavailable.Message);
                return;
            }

            if (!decision.Allowed)
            {
                var refused = WalletErrors.LimitExceeded(decision);
                Reject(refused.Code, refused.Message);
                return;
            }
        }

        var placed = await holds.PlaceAsync(
            new PlaceHoldCommand(wallet.Id, amount, command.Description, command.Reference, TimeSpan.FromSeconds(command.LifetimeSeconds), holdId),
            cancellationToken);
        if (placed.IsFailure)
        {
            if (limited)
            {
                await limits.ReleaseAsync(limitKey, CancellationToken.None);
            }

            Reject(placed.Error.Code, placed.Error.Message);
            return;
        }

        LogPlaced(logger, command.HoldId, command.AmountPaisa, purpose);
        replies.Send(new LedgerContracts.HoldPlaced(command.HoldId, placed.Value.AccountId.Value, placed.Value.Amount.MinorUnits, placed.Value.ExpiresAt), context);
    }

    public async Task HandleAsync(LedgerContracts.CaptureHold command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        void Fail(string code, string reason)
        {
            LogCaptureFailed(logger, command.HoldId, code);
            replies.Send(new LedgerContracts.HoldCaptureFailed(command.HoldId, command.TransactionId, code, reason), context);
        }

        if (!Enum.TryParse<JournalType>(command.Purpose, out var purpose) || !Enum.IsDefined(purpose))
        {
            Fail("ledger.invalid_purpose", $"Unknown purpose '{command.Purpose}'.");
            return;
        }

        var lines = new List<PostingLine>();
        foreach (var credit in command.Credits)
        {
            if (ChartOfAccounts.SystemAccounts.FirstOrDefault(definition => definition.Code == credit.SystemAccount) is not { } account || credit.AmountPaisa <= 0)
            {
                Fail("ledger.invalid_credit", $"Cannot credit {credit.AmountPaisa} paisa to '{credit.SystemAccount}'.");
                return;
            }

            lines.Add(PostingLine.Credit(AccountRef.System(account), Money.FromPaisa(credit.AmountPaisa)));
        }

        var total = command.Credits.Sum(credit => credit.AmountPaisa);
        var captured = await holds.CaptureAsync(
            new CaptureHoldCommand(new HoldId(command.HoldId), new TransactionId(command.TransactionId), Money.FromPaisa(total), purpose, command.Description, lines),
            cancellationToken);
        if (captured.IsFailure)
        {
            Fail(captured.Error.Code, captured.Error.Message);
            return;
        }

        LogCaptured(logger, command.HoldId, total);
        replies.Send(new LedgerContracts.HoldCaptured(command.HoldId, command.TransactionId, total), context);
    }

    public async Task HandleAsync(LedgerContracts.ReleaseHold command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var holdId = new HoldId(command.HoldId);
        var current = await holds.FindAsync(holdId, cancellationToken);
        if (current is null)
        {
            // Never placed (the place was rejected): nothing to return.
            replies.Send(new LedgerContracts.HoldReleased(command.HoldId, "NotPlaced"), context);
            return;
        }

        switch (current.Status)
        {
            case HoldStatus.Captured:
                // Too late: the money already left. Say what really happened.
                replies.Send(new LedgerContracts.HoldCaptured(command.HoldId, current.CaptureTransactionId?.Value ?? Guid.Empty, current.CapturedAmount), context);
                return;
            case HoldStatus.Released or HoldStatus.Expired:
                replies.Send(new LedgerContracts.HoldReleased(command.HoldId, current.Status.ToString()), context);
                return;
        }

        var released = await holds.ReleaseAsync(holdId, cancellationToken);
        if (released.IsFailure)
        {
            // It changed under us (expired or captured a moment ago): answer from its state now.
            if ((await holds.FindAsync(holdId, cancellationToken))?.Status is HoldStatus.Released or HoldStatus.Expired)
            {
                replies.Send(new LedgerContracts.HoldReleased(command.HoldId, "Expired"), context);
                return;
            }

            throw new InvalidOperationException($"Hold {command.HoldId} could not be released: {released.Error.Code}");
        }

        await limits.ReleaseAsync(new TransactionId(command.HoldId), CancellationToken.None);
        LogReleased(logger, command.HoldId, command.Reason);
        replies.Send(new LedgerContracts.HoldReleased(command.HoldId, "Released"), context);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Placed hold {HoldId}: {AmountPaisa} paisa for {Purpose}")]
    private static partial void LogPlaced(ILogger logger, Guid holdId, long amountPaisa, JournalType purpose);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused hold {HoldId}: {Code}")]
    private static partial void LogRejected(ILogger logger, Guid holdId, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured hold {HoldId}: {AmountPaisa} paisa")]
    private static partial void LogCaptured(ILogger logger, Guid holdId, long amountPaisa);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not capture hold {HoldId}: {Code}")]
    private static partial void LogCaptureFailed(ILogger logger, Guid holdId, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Released hold {HoldId}: {Reason}")]
    private static partial void LogReleased(ILogger logger, Guid holdId, string reason);
}
