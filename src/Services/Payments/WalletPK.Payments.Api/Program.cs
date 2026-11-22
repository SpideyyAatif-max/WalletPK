using WalletPK.Observability;
using WalletPK.Payments.Api;
using WalletPK.Payments.Api.Endpoints;
using WalletPK.Payments.Infrastructure;
using WalletPK.Sagas;
using WalletPK.Web;
using WalletPK.Web.Authentication;

// WalletPK Payments service.
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("payments");
builder.Services.AddWalletPkWeb();
builder.Services.AddPaymentsServices();

// Customer calls need a valid access token, checked here as well as at the gateway (defence in depth).
// Raast's callbacks are anonymous but signed (checked by the gateway and again here).
builder.Services.AddWalletPkJwtBearer();
builder.Services.AddAuthorizationBuilder().AddWalletPkPolicies();

var app = builder.Build();

app.UseWalletPkWeb();
app.UseWalletPkObservability();

app.UseAuthentication();
app.UseAuthorization();

await app.MigrateDatabaseIfConfiguredAsync();

app.MapBankTransferEndpoints();
app.MapWalletPaymentEndpoints();
app.MapRaastWebhookEndpoints();
app.MapSagaExplorer("/api/payments", WalletPkPolicies.Operations);

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
