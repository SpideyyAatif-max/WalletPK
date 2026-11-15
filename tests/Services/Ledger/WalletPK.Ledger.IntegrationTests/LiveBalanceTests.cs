using System.Net;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Api.Realtime;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>
/// M4.7: a browser connected to /hubs/wallet (WebSockets, token in the query string, as the web app
/// does it) gets every new entry on its own wallet live, and nobody else's.
/// </summary>
[Collection(LedgerCollection.Name)]
public class LiveBalanceTests(LedgerApiFactory factory)
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

    /// <summary>A signed-in browser tab: its hub connection and everything pushed to it.</summary>
    private sealed class Tab : IAsyncDisposable
    {
        private readonly HubConnection _connection;

        public Tab(LedgerApiFactory factory, UserId userId, string? phone)
        {
            var server = factory.Server;
            var token = factory.CreateToken(userId, phone);
            _connection = new HubConnectionBuilder()
                .WithUrl(new Uri(server.BaseAddress, WalletHub.Path), options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();

                    // A browser cannot set headers on a WebSocket, so the JS client puts the token in the URL.
                    options.WebSocketFactory = async (context, cancellationToken) =>
                    {
                        var uri = new UriBuilder(context.Uri);
                        uri.Query = $"{uri.Query.TrimStart('?')}&access_token={Uri.EscapeDataString(token)}".TrimStart('&');
                        return await server.CreateWebSocketClient().ConnectAsync(uri.Uri, cancellationToken);
                    };
                })
                .AddJsonProtocol(protocol => protocol.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
                .Build();
            _connection.On<LiveWalletEntry>(nameof(IWalletClient.EntryPosted), entry => Pushed.Writer.TryWrite(entry));
        }

        public Channel<LiveWalletEntry> Pushed { get; } = Channel.CreateUnbounded<LiveWalletEntry>();

        public Task StartAsync() => _connection.StartAsync(Cancel);

        /// <summary>The first push that matches, skipping older ones (the cash-in that funded the wallet can still be on its way).</summary>
        public async Task<LiveWalletEntry> NextAsync(Func<LiveWalletEntry, bool> match)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            timeout.CancelAfter(_wait);
            while (true)
            {
                var entry = await Pushed.Reader.ReadAsync(timeout.Token);
                if (match(entry))
                {
                    return entry;
                }
            }
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private async Task<Tab> OpenTabAsync(Customer customer)
    {
        var tab = new Tab(factory, customer.Id, customer.Phone);
        await tab.StartAsync();
        return tab;
    }

    /// <summary>
    /// The live consumer starts at the newest message once Kafka has given it its partitions, which takes
    /// a few seconds after the service starts. Cash-ins to a probe wallet tell when it is listening.
    /// </summary>
    private async Task WaitUntilListeningAsync()
    {
        var probe = await factory.CustomerAsync();
        await using var tab = await OpenTabAsync(probe);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            await factory.CashInAsync(probe.WalletId, 1);
            using var poll = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            poll.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await tab.Pushed.Reader.ReadAsync(poll.Token);
                return;
            }
            catch (OperationCanceledException) when (!Cancel.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                // Not assigned yet; try again.
            }
        }
    }

    [Fact]
    public async Task Both_sides_of_a_transfer_see_their_new_balance_live_and_nobody_else_does()
    {
        await WaitUntilListeningAsync();
        var sender = await factory.CustomerAsync(rupees: 1_000);
        var recipient = await factory.CustomerAsync();
        var bystander = await factory.CustomerAsync();

        await using var senderTab = await OpenTabAsync(sender);
        await using var recipientTab = await OpenTabAsync(recipient);
        await using var bystanderTab = await OpenTabAsync(bystander);

        using (var sent = await sender.SendAsync(recipient.Phone, 25_000, NewKey()))
        {
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var outgoing = await senderTab.NextAsync(entry => entry.Entry.Type == JournalType.P2PTransfer);
        outgoing.WalletId.ShouldBe(sender.WalletId);
        outgoing.WalletType.ShouldBe("CustomerWallet");
        outgoing.Sequence.ShouldBe(2);
        outgoing.Balance.MinorUnits.ShouldBe(await factory.BalanceAsync(sender.WalletId));
        outgoing.Entry.Direction.ShouldBe("Out");
        outgoing.Entry.Title.ShouldBe($"Sent to {WalletPK.Common.Primitives.PakistaniMobileNumber.Mask(recipient.Phone)}");
        outgoing.Entry.Amount.MinorUnits.ShouldBe(25_000);

        var incoming = await recipientTab.NextAsync(_ => true);
        incoming.WalletId.ShouldBe(recipient.WalletId);
        incoming.Sequence.ShouldBe(1);
        incoming.Balance.MinorUnits.ShouldBe(25_000);
        incoming.Entry.Direction.ShouldBe("In");
        incoming.Entry.TransactionId.ShouldBe(outgoing.Entry.TransactionId);

        // Give a wrong push time to show up.
        await Task.Delay(500, Cancel);
        bystanderTab.Pushed.Reader.TryRead(out _).ShouldBeFalse();
        senderTab.Pushed.Reader.TryRead(out _).ShouldBeFalse("one entry, one push");
    }

    [Fact]
    public async Task The_hub_needs_a_valid_access_token()
    {
        using var client = factory.CreateClient();

        using var anonymous = await client.PostAsync(new Uri($"{WalletHub.Path}/negotiate?negotiateVersion=1", UriKind.Relative), null, Cancel);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var token = factory.CreateToken(UserId.New(), NewPhone());
        using var withQueryToken = await client.PostAsync(new Uri($"{WalletHub.Path}/negotiate?negotiateVersion=1&access_token={token}", UriKind.Relative), null, Cancel);
        withQueryToken.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
