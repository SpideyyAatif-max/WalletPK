using WalletPK.Bills.Api;
using WalletPK.Bills.Api.Endpoints;
using WalletPK.Bills.Infrastructure;
using WalletPK.Observability;
using WalletPK.Sagas;
using WalletPK.Web;
using WalletPK.Web.Authentication;

// WalletPK Bill Payment service.
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("bills");
builder.Services.AddWalletPkWeb();
builder.Services.AddBillsServices();

// Customer and operations calls need a valid access token, checked here as well as at the gateway.
builder.Services.AddWalletPkJwtBearer();
builder.Services.AddAuthorizationBuilder().AddWalletPkPolicies();

var app = builder.Build();

app.UseWalletPkWeb();
app.UseWalletPkObservability();

app.UseAuthentication();
app.UseAuthorization();

await app.MigrateDatabaseIfConfiguredAsync();

app.MapBillEndpoints();
app.MapBillOperationsEndpoints();
app.MapSagaExplorer("/api/bills", WalletPkPolicies.Operations);

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
