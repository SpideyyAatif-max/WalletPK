using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;

namespace WalletPK.Common.UnitTests.Domain;

public class AggregateRootTests
{
    [Fact]
    public void Raised_events_are_recorded_until_cleared()
    {
        var wallet = new TestWallet(WalletId.New());

        wallet.Open();

        wallet.DomainEvents.Count.ShouldBe(1);
        wallet.DomainEvents.First().ShouldBeOfType<WalletOpened>();

        wallet.ClearDomainEvents();
        wallet.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Each_event_gets_a_unique_id()
    {
        var now = DateTimeOffset.UtcNow;

        new WalletOpened(now).EventId.ShouldNotBe(new WalletOpened(now).EventId);
    }

    private sealed record WalletOpened(DateTimeOffset At) : DomainEvent(At);

    private sealed class TestWallet(WalletId id) : AggregateRoot<WalletId>(id)
    {
        public void Open() => Raise(new WalletOpened(DateTimeOffset.UtcNow));
    }
}
