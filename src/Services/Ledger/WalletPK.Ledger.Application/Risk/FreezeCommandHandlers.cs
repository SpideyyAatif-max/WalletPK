using Microsoft.Extensions.Logging;
using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Holds;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Messaging.Abstractions;
using LedgerContracts = WalletPK.Contracts.Ledger;

namespace WalletPK.Ledger.Application.Risk;

/// <summary>
/// Freezes and unfreezes all of a user's wallets for the Fraud service (M10.6): customer wallet, merchant
/// wallet and agent float. A frozen wallet can still receive money; nothing can leave it.
/// </summary>
public sealed partial class FreezeCommandHandlers(
    IAccountRepository accounts,
    ILedgerUnitOfWork unitOfWork,
    IReplyOutbox replies,
    TimeProvider timeProvider,
    ILogger<FreezeCommandHandlers> logger) :
    IIntegrationEventHandler<LedgerContracts.FreezeWallets>,
    IIntegrationEventHandler<LedgerContracts.UnfreezeWallets>
{
    private static readonly AccountType[] WalletTypes = [AccountType.CustomerWallet, AccountType.MerchantWallet, AccountType.AgentWallet];

    public Task HandleAsync(LedgerContracts.FreezeWallets command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ChangeAsync(command.RequestId, command.UserId, freeze: true, command.Reason, context, cancellationToken);
    }

    public Task HandleAsync(LedgerContracts.UnfreezeWallets command, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ChangeAsync(command.RequestId, command.UserId, freeze: false, command.Reason, context, cancellationToken);
    }

    private async Task ChangeAsync(Guid requestId, Guid userId, bool freeze, string reason, EventContext context, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var owner = new UserId(userId);
        var ids = new List<AccountId>();
        foreach (var type in WalletTypes)
        {
            if (await accounts.GetWalletByOwnerAsync(owner, type, cancellationToken) is { } wallet)
            {
                ids.Add(wallet.Id);
            }
        }

        // Loaded again with row locks and tracked, so the change is saved (and waits for any posting in flight).
        var wallets = ids.Count == 0 ? [] : await accounts.GetManyAsync(ids, cancellationToken);
        foreach (var wallet in wallets)
        {
            if (freeze)
            {
                wallet.Freeze(now);
            }
            else
            {
                wallet.Unfreeze(now);
            }
        }

        var count = wallets.Count;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        LogChanged(logger, userId, freeze, count, reason);
        replies.Send(new LedgerContracts.WalletsFreezeChanged(requestId, userId, freeze, count, now), context);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wallets of user {UserId} frozen: {Frozen} ({Count} wallets): {Reason}")]
    private static partial void LogChanged(ILogger logger, Guid userId, bool frozen, int count, string reason);
}
