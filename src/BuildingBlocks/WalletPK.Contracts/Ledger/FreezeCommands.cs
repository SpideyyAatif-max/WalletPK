using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Ledger;

// Freezing (M10.6), driven by the Fraud service: a frozen wallet can still receive money but nothing
// can leave it. Both commands cover every wallet the user owns (customer, merchant, agent float) and are
// idempotent: freezing a frozen wallet, or unfreezing an active one, changes nothing and is answered again.

/// <summary>Freezes all of the user's wallets; answered by <see cref="WalletsFreezeChanged"/>.</summary>
/// <param name="RequestId">Echoed in the reply.</param>
/// <param name="UserId">Whose wallets.</param>
/// <param name="Reason">Why, for the audit trail ("Rule sim-swap-drain", "Case 42").</param>
[IntegrationEvent("ledger.freeze_wallets", KafkaTopics.LedgerCommands)]
public sealed record FreezeWallets(Guid RequestId, Guid UserId, string Reason) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}

/// <summary>Unfreezes all of the user's wallets; answered by <see cref="WalletsFreezeChanged"/>.</summary>
[IntegrationEvent("ledger.unfreeze_wallets", KafkaTopics.LedgerCommands)]
public sealed record UnfreezeWallets(Guid RequestId, Guid UserId, string Reason) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}

/// <summary>The user's wallets are now frozen (<paramref name="Frozen"/>) or active; <paramref name="WalletCount"/> is how many they have.</summary>
/// <param name="RequestId">The command's request.</param>
/// <param name="UserId">Whose wallets.</param>
/// <param name="Frozen">True after a freeze, false after an unfreeze.</param>
/// <param name="WalletCount">How many wallets the user has (0: none yet).</param>
/// <param name="ChangedAt">When.</param>
[IntegrationEvent("ledger.wallets_freeze_changed", KafkaTopics.LedgerReplies)]
public sealed record WalletsFreezeChanged(Guid RequestId, Guid UserId, bool Frozen, int WalletCount, DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}
