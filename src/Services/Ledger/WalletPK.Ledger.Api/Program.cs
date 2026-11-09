using WalletPK.Ledger.Api;
using WalletPK.Ledger.Api.Endpoints;
using WalletPK.Ledger.Infrastructure;
using WalletPK.Observability;
using WalletPK.Web;
using WalletPK.Web.Authentication;
using WalletPK.Web.Idempotency;

// WalletPK Wallet & Ledger service.
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("ledger");
builder.Services.AddWalletPkWeb();
builder.Services.AddLedgerServices();

// Every wallet call needs a valid access token, checked here as well as at the gateway (defence in depth).
builder.Services.AddWalletPkJwtBearer();
builder.Services.AddAuthorizationBuilder().AddWalletPkPolicies();

var app = builder.Build();

app.UseWalletPkWeb();
app.UseWalletPkObservability();

app.UseAuthentication();
app.UseAuthorization();

// Replays the first response for a repeated Idempotency-Key on endpoints marked RequireIdempotencyKey() (M3.7).
app.UseWalletPkIdempotency();

await app.MigrateDatabaseIfConfiguredAsync();

app.MapWalletEndpoints();
app.MapLedgerOperationsEndpoints();

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
