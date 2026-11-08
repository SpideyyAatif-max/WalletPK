using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Messaging.Serialization;
using WalletPK.Messaging.UnitTests.Events;

namespace WalletPK.Messaging.UnitTests;

public class JsonEventSerializerTests
{
    private readonly JsonEventSerializer _serializer = new(
        new IntegrationEventRegistry().Register<MoneyTransferred>().Register<BillPaid>().Register<BillPaidV2>());

    private static MoneyTransferred SampleTransfer() => new(
        TransactionId.New(),
        WalletId.New(),
        WalletId.New(),
        Money.FromRupees(2500.75m),
        TransferChannel.App);

    [Fact]
    public void Wrap_fills_the_routing_metadata()
    {
        var transfer = SampleTransfer();

        var envelope = _serializer.Wrap(transfer, correlationId: "corr-1", causationId: "cmd-9");

        envelope.EventId.ShouldBe(transfer.EventId);
        envelope.EventType.ShouldBe("ledger.money_transferred");
        envelope.EventVersion.ShouldBe(1);
        envelope.Topic.ShouldBe(KafkaTopics.LedgerEvents);
        envelope.PartitionKey.ShouldBe(transfer.FromWalletId.ToString());
        envelope.OccurredAt.ShouldBe(transfer.OccurredAt);
        envelope.CorrelationId.ShouldBe("corr-1");
        envelope.CausationId.ShouldBe("cmd-9");
    }

    [Fact]
    public void Payload_is_camel_case_json_with_exact_money_and_readable_enums()
    {
        var envelope = _serializer.Wrap(SampleTransfer());

        envelope.Payload.ShouldContain("\"fromWalletId\":");
        envelope.Payload.ShouldContain("\"amount\":{\"minorUnits\":250075,\"currency\":\"PKR\"}");
        envelope.Payload.ShouldContain("\"channel\":\"App\"");
        envelope.Payload.ShouldNotContain("partitionKey");
    }

    [Fact]
    public void Unwrap_restores_an_identical_event()
    {
        var original = SampleTransfer();

        var envelope = _serializer.Wrap(original);
        _serializer.TryUnwrap(envelope, out var restored).ShouldBeTrue();

        restored.ShouldBe(original);
    }

    [Fact]
    public void Each_version_is_restored_as_its_own_type()
    {
        var v2 = new BillPaidV2("0400012345678", Money.FromRupees(3200m), "KE");

        _serializer.TryUnwrap(_serializer.Wrap(v2), out var restored).ShouldBeTrue();

        restored.ShouldBeOfType<BillPaidV2>().BillerCode.ShouldBe("KE");
    }

    [Fact]
    public void Unknown_event_types_are_skipped_not_failed()
    {
        var foreign = new EventEnvelope(
            Guid.CreateVersion7(), "fraud.rule_changed", 1, KafkaTopics.FraudAlerts, "k",
            DateTimeOffset.UtcNow, null, null, "{}");

        _serializer.TryUnwrap(foreign, out var restored).ShouldBeFalse();
        restored.ShouldBeNull();
    }
}
