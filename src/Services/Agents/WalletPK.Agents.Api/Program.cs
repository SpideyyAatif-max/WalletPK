using WalletPK.Agents.Api;
using WalletPK.Agents.Api.Endpoints;
using WalletPK.Agents.Infrastructure;
using WalletPK.Observability;
using WalletPK.Sagas;
using WalletPK.Web;
using WalletPK.Web.Authentication;

// WalletPK Agent Network service.
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("agents");
builder.Services.AddWalletPkWeb();
builder.Services.AddAgentServices();

// Customer, agent and operations calls need a valid access token, checked here as well as at the gateway.
builder.Services.AddWalletPkJwtBearer();
builder.Services.AddAuthorizationBuilder().AddWalletPkPolicies();

var app = builder.Build();

app.UseWalletPkWeb();
app.UseWalletPkObservability();

app.UseAuthentication();
app.UseAuthorization();

await app.MigrateDatabaseIfConfiguredAsync();

app.MapAgentEndpoints();
app.MapCashOutEndpoints();
app.MapSagaExplorer("/api/agents", WalletPkPolicies.Operations);

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
