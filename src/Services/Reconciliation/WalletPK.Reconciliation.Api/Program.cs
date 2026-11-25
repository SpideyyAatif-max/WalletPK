using WalletPK.Observability;
using WalletPK.Reconciliation.Api;
using WalletPK.Reconciliation.Api.Endpoints;
using WalletPK.Reconciliation.Infrastructure;
using WalletPK.Web;
using WalletPK.Web.Authentication;

// WalletPK Reconciliation service (M12): every day, each bank's, biller's and network's settlement file is
// matched against the bank transfers, bill payments and top-ups WalletPK sent them; Finance works the
// breaks, and every run and decision goes on a hash-chained audit log.
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("reconciliation");
builder.Services.AddWalletPkWeb();
builder.Services.AddReconciliationServices();

// Every call needs a valid access token, checked here as well as at the gateway.
builder.Services.AddWalletPkJwtBearer();
builder.Services.AddAuthorizationBuilder().AddWalletPkPolicies();

var app = builder.Build();

app.UseWalletPkWeb();
app.UseWalletPkObservability();

app.UseAuthentication();
app.UseAuthorization();

await app.MigrateDatabaseIfConfiguredAsync();

app.MapReconciliationEndpoints();

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
