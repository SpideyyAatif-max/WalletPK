using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Infrastructure;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using WalletPK.Messaging;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Inbox;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;
using IdentityContracts = WalletPK.Contracts.Identity;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>
/// M4.1-M4.3 through a real Kafka broker: the outbox relay publishes what the Ledger posts, keyed by
/// wallet; the Ledger consumes identity.events through its inbox and opens wallets for new users.
/// </summary>
[Collection(LedgerCollection.Name)]
public class EventPipelineTests(LedgerApiFactory factory)
{
    private IntegrationEventRegistry Registry => factory.Services.GetRequiredService<IntegrationEventRegistry>();

    private static IdentityContracts.UserRegistered NewUser(string phone) =>
        new(Guid.CreateVersion7(), phone, "L0", DateTimeOffset.UtcNow);

    private async Task<bool> HasWalletAsync(Guid userId, string phone)
    {
        using var client = factory.CreateClientFor(new UserId(userId), phone);
        using var response = await client.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel);
        return response.StatusCode == HttpStatusCode.OK;
    }

    private Task<int> InboxRowsAsync(Guid eventId) =>
        factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().Set<InboxMessage>()
            .CountAsync(row => row.EventId == eventId && row.Consumer == LedgerInfrastructureExtensions.LedgerConsumerGroup, Cancel));

    [Fact]
    public async Task A_transfer_reaches_kafka_as_one_journal_event_and_one_event_per_wallet_keyed_by_wallet()
    {
        var sender = await factory.CustomerAsync(rupees: 5_000);
        var recipient = await factory.CustomerAsync();
        var key = NewKey();
        using (var sent = await sender.SendAsync(recipient.Phone, 150_000, key))
        {
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var transactionId = WalletService.TransactionIdFor(sender.Id, key).Value;
        bool Mine(ReadMessage message) => message.Payload.TryGetProperty("transactionId", out var id) && id.GetGuid() == transactionId;

        var messages = factory.ReadUntil(KafkaTopics.LedgerEvents, read => read.Count(Mine) == 3).Where(Mine).ToList();

        var journal = messages.Single(message => message.EventType == "ledger.journal_posted");
        journal.Key.ShouldBe(transactionId.ToString());
        journal.Payload.GetProperty("amountPaisa").GetInt64().ShouldBe(150_870);
        journal.Payload.GetProperty("entries").GetArrayLength().ShouldBe(4);

        var walletEvents = messages.Where(message => message.EventType == "ledger.wallet_entry_posted")
            .ToDictionary(message => message.Payload.GetProperty("walletNumber").GetString()!);
        var outgoing = walletEvents[sender.Phone];
        outgoing.Key.ShouldBe(sender.WalletId.ToString());
        outgoing.Payload.GetProperty("sequence").GetInt64().ShouldBe(2); // 1 was the cash-in
        outgoing.Payload.GetProperty("direction").GetString().ShouldBe("Debit");
        outgoing.Payload.GetProperty("amountPaisa").GetInt64().ShouldBe(150_870);
        outgoing.Payload.GetProperty("balanceAfterPaisa").GetInt64().ShouldBe(500_000 - 150_870);
        outgoing.Payload.GetProperty("counterpartyWalletNumber").GetString().ShouldBe(recipient.Phone);

        var incoming = walletEvents[recipient.Phone];
        incoming.Key.ShouldBe(recipient.WalletId.ToString());
        incoming.Payload.GetProperty("sequence").GetInt64().ShouldBe(1);
        incoming.Payload.GetProperty("counterpartyWalletNumber").GetString().ShouldBe(sender.Phone);

        // And the outbox rows were marked as published.
        await KafkaTestKit.EventuallyAsync(() => factory.InScopeAsync(async services =>
            !await services.GetRequiredService<LedgerDbContext>().OutboxMessages
                .AnyAsync(message => message.PublishedAt == null, Cancel)));
    }

    [Fact]
    public async Task One_wallets_events_arrive_on_one_partition_in_sequence_order()
    {
        var sender = await factory.CustomerAsync(rupees: 1_000);
        var recipients = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => factory.CustomerAsync()));
        for (var i = 0; i < 9; i++)
        {
            using var sent = await sender.SendAsync(recipients[i % 3].Phone, 1_000 + i, NewKey());
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        bool Mine(ReadMessage message) => message.Key == sender.WalletId.ToString();
        var events = factory.ReadUntil(KafkaTopics.LedgerEvents, read => read.Count(Mine) == 11).Where(Mine).ToList();

        // The wallet was opened first, then came its ten entries, all on the wallet's partition.
        events.Select(message => message.Partition).Distinct().ShouldHaveSingleItem();
        events[0].EventType.ShouldBe("ledger.wallet_opened");
        events[0].Payload.GetProperty("walletNumber").GetString().ShouldBe(sender.Phone);
        events[0].Payload.GetProperty("ownerId").GetGuid().ShouldBe(sender.Id.Value);
        events.Skip(1).Select(message => message.EventType).Distinct().ShouldBe(["ledger.wallet_entry_posted"]);
        events.Skip(1).Select(message => message.Payload.GetProperty("sequence").GetInt64()).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    }

    [Fact]
    public async Task A_new_user_gets_a_wallet_from_the_identity_event_without_asking()
    {
        var phone = NewPhone();
        var registered = NewUser(phone);

        await factory.PublishAsync(registered, Registry, correlationId: "signup-correlation");

        await KafkaTestKit.EventuallyAsync(() => HasWalletAsync(registered.UserId, phone), because: "the Ledger did not open the wallet");
        (await InboxRowsAsync(registered.EventId)).ShouldBe(1);

        // The wallet-opened audit row carries the correlation ID of the sign-up that caused it.
        var walletId = await factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().Accounts
            .Where(account => account.Alias == phone).Select(account => account.Id).SingleAsync(Cancel));
        var audit = await factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().AuditLog.AsNoTracking()
            .SingleAsync(record => record.EventType == "account.opened" && record.SubjectId == walletId.ToString(), Cancel));
        audit.CorrelationId.ShouldBe("signup-correlation");
    }

    [Fact]
    public async Task The_same_event_delivered_twice_is_processed_once()
    {
        var phone = NewPhone();
        var registered = NewUser(phone);
        var envelope = new JsonEventSerializer(Registry).Wrap(registered);

        // At-least-once delivery: the relay may send an event again after a crash.
        await factory.PublishEnvelopeAsync(envelope);
        await factory.PublishEnvelopeAsync(envelope);

        // A different event for the same user (for example a retried sign-up) is idempotent too.
        await factory.PublishAsync(registered with { EventId = Guid.CreateVersion7() }, Registry);

        await KafkaTestKit.EventuallyAsync(() => HasWalletAsync(registered.UserId, phone));
        await KafkaTestKit.EventuallyAsync(async () => await factory.InScopeAsync(services =>
            services.GetRequiredService<LedgerDbContext>().Set<InboxMessage>().CountAsync(row => row.EventType == "identity.user_registered", Cancel)) > 0);

        // Give the consumer time to see the redelivery and the second event, then check.
        var marker = NewUser(NewPhone());
        await factory.PublishAsync(marker, Registry);
        await KafkaTestKit.EventuallyAsync(() => HasWalletAsync(marker.UserId, marker.PhoneNumber));

        (await InboxRowsAsync(envelope.EventId)).ShouldBe(1);
        var wallets = await factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().Accounts
            .CountAsync(account => account.Alias == phone, Cancel));
        wallets.ShouldBe(1);
    }

    [Fact]
    public async Task A_malformed_message_is_skipped_and_does_not_block_the_partition()
    {
        var phone = NewPhone();
        var registered = NewUser(phone);

        // Same key, so same partition: the good event sits right behind the bad one.
        await factory.ProduceRawAsync(KafkaTopics.IdentityEvents, registered.UserId.ToString(), "not an event, no headers");
        await factory.PublishAsync(registered, Registry);

        await KafkaTestKit.EventuallyAsync(() => HasWalletAsync(registered.UserId, phone), because: "the bad message blocked the partition");
    }

    [Fact]
    public async Task Sign_up_and_the_app_opening_the_wallet_at_the_same_time_still_make_one_wallet()
    {
        var phone = NewPhone();
        var registered = NewUser(phone);
        using var client = factory.CreateClientFor(new UserId(registered.UserId), phone);

        var publish = factory.PublishAsync(registered, Registry);
        var opens = Enumerable.Range(0, 5).Select(_ => client.PostAsync(new Uri("/api/wallets", UriKind.Relative), null, Cancel)).ToArray();
        await publish;
        var responses = await Task.WhenAll(opens);

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK);
        await KafkaTestKit.EventuallyAsync(async () => await InboxRowsAsync(registered.EventId) == 1);
        var wallets = await factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().Accounts
            .CountAsync(account => account.Alias == phone, Cancel));
        wallets.ShouldBe(1);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
