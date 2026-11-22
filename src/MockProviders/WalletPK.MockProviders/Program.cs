using WalletPK.MockProviders;
using WalletPK.MockProviders.Storage;
using WalletPK.Observability;
using WalletPK.Web;

// WalletPK mock external providers (M5, M11): Raast and four banks, four billers, four mobile networks,
// NADRA and SMS and email gateways, each controllable at runtime (latency, errors, timeouts, outages) and producing settlement files.
// Internal only: services call it directly; it is not routed through the gateway.
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("mock-providers");
builder.Services.AddWalletPkWeb();
builder.Services.AddMockProviders();

var app = builder.Build();

app.UseWalletPkWeb();
app.UseWalletPkObservability();

await app.Services.GetRequiredService<MockDatabase>().EnsureSchemaAsync(CancellationToken.None);

app.MapMockProviders();

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
