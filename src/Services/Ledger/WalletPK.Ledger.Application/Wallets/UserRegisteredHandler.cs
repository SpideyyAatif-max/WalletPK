using Microsoft.Extensions.Logging;
using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Messaging.Abstractions;
using IdentityContracts = WalletPK.Contracts.Identity;

namespace WalletPK.Ledger.Application.Wallets;

/// <summary>
/// Opens a customer wallet, numbered with the user's mobile number, as soon as Identity reports a new
/// registration (M4.3): sign up and the wallet is simply there. Opening is idempotent, so the same
/// event twice (or the app also calling POST /api/wallets) still leaves exactly one wallet.
/// </summary>
public sealed partial class UserRegisteredHandler(WalletService wallets, ILogger<UserRegisteredHandler> logger)
    : IIntegrationEventHandler<IdentityContracts.UserRegistered>
{
    public async Task HandleAsync(IdentityContracts.UserRegistered integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var opened = await wallets.OpenAsync(new UserId(integrationEvent.UserId), integrationEvent.PhoneNumber, cancellationToken);
        if (opened.IsFailure)
        {
            // A bad phone number will not get better by retrying; log it for support instead of blocking the partition.
            LogCannotOpen(logger, integrationEvent.UserId, opened.Error.Code);
            return;
        }

        if (opened.Value.Created)
        {
            LogOpened(logger, opened.Value.Wallet.WalletId, integrationEvent.UserId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened wallet {WalletId} for new user {UserId}")]
    private static partial void LogOpened(ILogger logger, AccountId walletId, Guid userId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not open a wallet for new user {UserId}: {ErrorCode}")]
    private static partial void LogCannotOpen(ILogger logger, Guid userId, string errorCode);
}
