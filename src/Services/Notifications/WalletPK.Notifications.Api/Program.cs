using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using StackExchange.Redis;
using WalletPK.Notifications.Api;
using WalletPK.Notifications.Api.Endpoints;
using WalletPK.Notifications.Api.Realtime;
using WalletPK.Notifications.Application;
using WalletPK.Notifications.Infrastructure;
using WalletPK.Observability;
using WalletPK.Web;
using WalletPK.Web.Authentication;

// WalletPK Notifications service (M11): notifications from every service's events, in English and Urdu,
// in the app, by simulated SMS and email, and live over SignalR (wallet and Ops hubs, Redis backplane).
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("notifications");
builder.Services.AddWalletPkWeb();
builder.Services.AddNotificationsServices();

// Live pushes. With several instances, the Redis backplane carries each push to the instance holding the
// user's connection; without Redis (one instance in development) pushes stay local.
var signalR = builder.Services.AddSignalR()
    .AddJsonProtocol(protocol => protocol.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
if (builder.Configuration.GetConnectionString("Redis") is { Length: > 0 })
{
    signalR.AddStackExchangeRedis();

    // Read when the hubs start, so settings added after this point (environment, tests) count.
    builder.Services.AddOptions<RedisOptions>().Configure<IConfiguration>((options, configuration) =>
    {
        options.Configuration = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis")!);
        options.Configuration.ChannelPrefix = RedisChannel.Literal("walletpk:signalr");
    });
}

builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
builder.Services.AddSingleton<SignalRRealtime>();
builder.Services.AddSingleton<LivePushQueue>();
builder.Services.AddSingleton<IRealtime>(provider => provider.GetRequiredService<LivePushQueue>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<LivePushQueue>());

// Every call needs a valid access token, checked here as well as at the gateway.
builder.Services.AddWalletPkJwtBearer();
builder.Services.AddAuthorizationBuilder().AddWalletPkPolicies();

var app = builder.Build();

app.UseWalletPkWeb();
app.UseWalletPkObservability();

app.UseAuthentication();
app.UseAuthorization();

await app.MigrateDatabaseIfConfiguredAsync();

app.MapNotificationEndpoints(app.Environment);
app.MapHub<WalletHub>(WalletHub.Path).RequireAuthorization(WalletPkPolicies.Authenticated);
app.MapHub<OpsHub>(OpsHub.Path).RequireAuthorization(WalletPkPolicies.Operations);

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
