using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Messaging.Events;

namespace WalletPK.Messaging.UnitTests.Events;

public enum TransferChannel
{
    App,
    Agent,
}

[IntegrationEvent("ledger.money_transferred", KafkaTopics.LedgerEvents)]
public sealed record MoneyTransferred(
    TransactionId TransactionId,
    WalletId FromWalletId,
    WalletId ToWalletId,
    Money Amount,
    TransferChannel Channel) : IntegrationEvent
{
    // All events of the sending wallet go to one partition, so they are consumed in order.
    public override string GetPartitionKey() => FromWalletId.ToString();
}

[IntegrationEvent("bills.bill_paid", KafkaTopics.BillsEvents)]
public sealed record BillPaid(string ConsumerNumber, Money Amount) : IntegrationEvent;

[IntegrationEvent("bills.bill_paid", KafkaTopics.BillsEvents, Version = 2)]
public sealed record BillPaidV2(string ConsumerNumber, Money Amount, string BillerCode) : IntegrationEvent;
