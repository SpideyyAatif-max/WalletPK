using WalletPK.Observability;
using WalletPK.Sagas;
using WalletPK.TopupMerchant.Api;
using WalletPK.TopupMerchant.Api.Endpoints;
using WalletPK.TopupMerchant.Infrastructure;
using WalletPK.Web;
using WalletPK.Web.Authentication;

// WalletPK Top-up & Merchant service.
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("topup-merchant");
builder.Services.AddWalletPkWeb();
builder.Services.AddTopupMerchantServices();

// Customer and operations calls need a valid access token, checked here as well as at the gateway.
builder.Services.AddWalletPkJwtBearer();
builder.Services.AddAuthorizationBuilder().AddWalletPkPolicies();

var app = builder.Build();

app.UseWalletPkWeb();
app.UseWalletPkObservability();

app.UseAuthentication();
app.UseAuthorization();

await app.MigrateDatabaseIfConfiguredAsync();

app.MapTopUpEndpoints();
app.MapTopUpOperationsEndpoints();
app.MapMerchantEndpoints();
app.MapSagaExplorer("/api/topups", WalletPkPolicies.Operations);

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
