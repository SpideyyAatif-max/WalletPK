using WalletPK.Messaging.Serialization;
using WalletPK.Messaging.UnitTests.Events;
using WalletPK.Messaging.UnitTests.InvalidEvents;

namespace WalletPK.Messaging.UnitTests;

public class IntegrationEventRegistryTests
{
    [Fact]
    public void Registers_every_event_in_a_namespace()
    {
        var registry = new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(MoneyTransferred).Assembly, typeof(MoneyTransferred).Namespace);

        registry.EventTypes.ShouldBe(
            new[] { typeof(MoneyTransferred), typeof(BillPaid), typeof(BillPaidV2) },
            ignoreOrder: true);
    }

    [Fact]
    public void Two_versions_of_one_event_map_to_different_types()
    {
        var registry = new IntegrationEventRegistry().Register<BillPaid>().Register<BillPaidV2>();

        registry.TryGetType("bills.bill_paid", 1, out var v1).ShouldBeTrue();
        registry.TryGetType("bills.bill_paid", 2, out var v2).ShouldBeTrue();
        v1.ShouldBe(typeof(BillPaid));
        v2.ShouldBe(typeof(BillPaidV2));
        registry.TryGetType("bills.bill_paid", 3, out _).ShouldBeFalse();
    }

    [Fact]
    public void Metadata_comes_from_the_attribute()
    {
        var metadata = new IntegrationEventRegistry().Register<MoneyTransferred>().GetMetadata(typeof(MoneyTransferred));

        metadata.Name.ShouldBe("ledger.money_transferred");
        metadata.Topic.ShouldBe(KafkaTopics.LedgerEvents);
        metadata.Version.ShouldBe(1);
    }

    [Theory]
    [InlineData(typeof(WithoutMetadata), "must be marked")]
    [InlineData(typeof(BadName), "must look like")]
    [InlineData(typeof(UnknownTopic), "is not a WalletPK topic")]
    [InlineData(typeof(string), "must be a concrete type")]
    public void Misconfigured_events_fail_fast(Type eventType, string expectedMessage)
    {
        var registry = new IntegrationEventRegistry();

        Should.Throw<InvalidOperationException>(() => registry.Register(eventType))
            .Message.ShouldContain(expectedMessage);
    }

    [Fact]
    public void Two_types_cannot_claim_the_same_name_and_version()
    {
        var registry = new IntegrationEventRegistry().Register<MoneyTransferred>();

        Should.Throw<InvalidOperationException>(() => registry.Register<DuplicateName>())
            .Message.ShouldContain("claim event 'ledger.money_transferred'");
    }

    [Fact]
    public void Unregistered_types_have_no_metadata()
    {
        Should.Throw<InvalidOperationException>(() => new IntegrationEventRegistry().GetMetadata(typeof(BillPaid)));
    }
}
