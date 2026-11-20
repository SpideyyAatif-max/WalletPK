using WalletPK.Fraud.Api;
using WalletPK.Fraud.Api.Endpoints;
using WalletPK.Fraud.Api.GrpcServices;
using WalletPK.Fraud.Infrastructure;
using WalletPK.Observability;
using WalletPK.Web;
using WalletPK.Web.Authentication;

// WalletPK Fraud & Risk service.
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("fraud");
builder.Services.AddWalletPkWeb();
builder.Services.AddFraudServices();
builder.Services.AddGrpc();
builder.Services.AddSingleton<ScoreMetrics>();

// Compliance calls need a valid access token, checked here as well as at the gateway.
builder.Services.AddWalletPkJwtBearer();
builder.Services.AddAuthorizationBuilder().AddWalletPkPolicies();

var app = builder.Build();

app.UseWalletPkWeb();
app.UseWalletPkObservability();

app.UseAuthentication();
app.UseAuthorization();

await app.MigrateDatabaseIfConfiguredAsync();

app.MapFraudEndpoints();

// Internal service-to-service API (HTTP/2 only; the gateway never routes here).
app.MapGrpcService<RiskGrpcService>();

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
