using Microsoft.Extensions.Options;
using Npgsql;
using WalletPK.MockProviders.Banks;
using WalletPK.MockProviders.Billers;
using WalletPK.MockProviders.Callbacks;
using WalletPK.MockProviders.Faults;
using WalletPK.MockProviders.Messaging;
using WalletPK.MockProviders.Nadra;
using WalletPK.MockProviders.Security;
using WalletPK.MockProviders.Settlement;
using WalletPK.MockProviders.Storage;
using WalletPK.MockProviders.Telcos;

namespace WalletPK.MockProviders;

public static class MockProvidersExtensions
{
    public static IServiceCollection AddMockProviders(this IServiceCollection services)
    {
        services.AddOptions<MockProvidersOptions>()
            .BindConfiguration(MockProvidersOptions.SectionName)
            .Validate(options => options.IsValid(), "MockProviders: ClientApiKeys and AdminApiKey (16+ characters each), settle delays (min <= max), Callbacks:MaxAttempts 1-50.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<MockProvidersOptions>>().Value);

        services.AddSingleton(provider => NpgsqlDataSource.Create(
            provider.GetRequiredService<IConfiguration>().GetConnectionString(MockDatabase.ConnectionName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{MockDatabase.ConnectionName} is required.")));
        services.AddSingleton<MockDatabase>();
        services.AddSingleton<OperationStore>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<FaultRegistry>();
        services.AddSingleton<IFaultDice, RandomFaultDice>();
        services.AddSingleton<FaultInjector>();

        services.AddSingleton<CallbackOutbox>();
        services.AddSingleton<CallbackSender>();
        services.AddHttpClient(CallbackSender.HttpClientName, (provider, client) =>
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<MockProvidersOptions>().Callbacks.TimeoutSeconds));
        services.AddHostedService<CallbackDispatcher>();
        services.AddHostedService<RaastSettler>();

        return services;
    }

    public static WebApplication MapMockProviders(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/", () => Results.Ok(new
        {
            service = "WalletPK mock providers (M5)",
            providers = Providers.All,
            apis = new[]
            {
                "GET  /raast/v1/aliases/{mobile}, /raast/v1/accounts/{iban}; POST /raast/v1/transfers; GET /raast/v1/transfers/{transferId}",
                "GET  /billers, /billers/{code}/bills/{consumerNumber}; POST /billers/{code}/payments; GET /billers/{code}/payments/{paymentId}",
                "GET  /telcos, /telcos/{code}/bundles; POST /telcos/{code}/topups; GET /telcos/{code}/topups/{topUpId}",
                "POST /nadra/v1/verifications",
                "GET  /settlement/{banks|billers|telcos}/{code}/{yyyy-MM-dd}",
                "POST /sms/v1/messages, /email/v1/messages; GET /sms/v1/messages?to=, /email/v1/messages?to=",
            },
            control = new[]
            {
                "GET /admin/faults; PUT|DELETE /admin/faults/{provider}; DELETE /admin/faults",
                "PUT|DELETE /admin/settlement/{kind}/{code}/{date}/mismatches; GET /admin/settlement/{kind}/{code}/{date}/injected",
                "PUT|GET /admin/nadra/citizens/{cnic}",
                "GET /admin/callbacks",
            },
            auth = $"Provider APIs need {ApiKeys.ClientHeader}; control APIs need {ApiKeys.AdminHeader}.",
        })).ExcludeFromDescription();

        app.MapGroup("/admin/callbacks").WithTags("Control: callbacks").RequireAdminKey()
            .MapGet("/", async (CallbackOutbox outbox, CancellationToken cancellationToken) => Results.Ok(await outbox.RecentAsync(100, cancellationToken)));

        app.MapFaultEndpoints();
        app.MapRaastEndpoints();
        app.MapBillerEndpoints();
        app.MapTelcoEndpoints();
        app.MapNadraEndpoints();
        app.MapSettlementEndpoints();
        app.MapMessagingEndpoints();
        return app;
    }
}
