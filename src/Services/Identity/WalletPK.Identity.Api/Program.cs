using WalletPK.Identity.Api;
using WalletPK.Identity.Api.Endpoints;
using WalletPK.Identity.Api.GrpcServices;
using WalletPK.Identity.Infrastructure;
using WalletPK.Observability;
using WalletPK.Web;

// WalletPK Identity & KYC service.
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("identity");
builder.Services.AddWalletPkWeb();
builder.Services.AddIdentityServices();
builder.Services.AddGrpc();

var app = builder.Build();

app.UseWalletPkWeb();
app.UseWalletPkObservability();
app.UseAuthentication();
app.UseAuthorization();

await app.MigrateDatabaseIfConfiguredAsync();

app.MapDiscoveryEndpoints();
app.MapRegistrationEndpoints();
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapKycAndLimitsEndpoints();

// Internal service-to-service API (HTTP/2 only; the gateway never routes here).
app.MapGrpcService<LimitsGrpcService>();

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
