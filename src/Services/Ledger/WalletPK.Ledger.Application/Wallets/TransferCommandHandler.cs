using Microsoft.Extensions.Logging;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Holds;
using WalletPK.Messaging.Abstractions;
using LedgerContracts = WalletPK.Contracts.Ledger;

namespace WalletPK.Ledger.Application.Wallets;

/// <summary>A refused transfer, remembered so the same transfer ID always gets the same answer.</summary>
/// <param name="TransactionId">The transfer ID.</param>
/// <param name="Code">Why.</param>
/// <param name="Reason">Why, for people.</param>
/// <param name="RejectedAt">When.</param>
public sealed record TransferRejection(Guid TransactionId, string Code, string Reason, DateTimeOffset RejectedAt);

/// <summary>Port: refused transfers (table transfer_rejections).</summary>
public interface ITransferRejections
{
    Task<TransferRejection?> FindAsync(Guid transactionId, CancellationToken cancellationToken);

    /// <summary>Saved with the handler's transaction.</summary>
    void Add(TransferRejection rejection);
}

/// <summary>
/// Answers <see cref="LedgerContracts.TransferFunds"/> from Payments (M6.1) with the same posting as
/// POST /api/wallets/transfers: one journal, fee and tax included, limits checked. Safe to repeat:
/// a transfer that went through answers with its receipt again, and a refused one with the same refusal,
/// even if the sender's balance has changed since (a late repeat never moves money after a "no").
/// </summary>
public sealed partial class TransferCommandHandler(
    WalletService wallets,
    ITransferRejections rejections,
    IReplyOutbox replies,
    TimeProvider timeProvider,
    ILogger<TransferCommandHandler> logger) : IIntegrationEventHandler<LedgerContracts.TransferFunds>
{
    public async Task HandleAsync(LedgerContracts.TransferFunds command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (await rejections.FindAsync(command.TransferId, cancellationToken) is { } refused)
        {
            replies.Send(new LedgerContracts.FundsTransferRejected(command.TransferId, command.ReplyTo, refused.Code, refused.Reason), context);
            return;
        }

        if (command.AmountPaisa <= 0)
        {
            Reject("wallet.amount_invalid", "The amount must be positive.");
            return;
        }

        var result = await wallets.TransferAsync(
            new TransferCommand(
                new UserId(command.SenderUserId),
                command.DeviceId,
                command.ToWalletNumber,
                Money.FromPaisa(command.AmountPaisa),
                new TransactionId(command.TransferId)),
            cancellationToken);

        if (result.IsFailure)
        {
            Reject(result.Error.Code, result.Error.Message);
            return;
        }

        var receipt = result.Value;
        replies.Send(
            new LedgerContracts.FundsTransferred(
                command.TransferId,
                command.ReplyTo,
                receipt.Amount.MinorUnits,
                receipt.Fee.MinorUnits,
                receipt.Tax.MinorUnits,
                receipt.ToWalletNumber,
                receipt.PostedAt),
            context);

        void Reject(string code, string reason)
        {
            LogRejected(logger, command.TransferId, code);
            rejections.Add(new TransferRejection(command.TransferId, code, reason, timeProvider.GetUtcNow()));
            replies.Send(new LedgerContracts.FundsTransferRejected(command.TransferId, command.ReplyTo, code, reason), context);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Transfer {TransferId} refused: {Code}")]
    private static partial void LogRejected(ILogger logger, Guid transferId, string code);
}
