using WalletPK.Gateway.Authentication;
using WalletPK.Gateway.Cors;
using WalletPK.Gateway.Proxy;
using WalletPK.Gateway.RateLimiting;
using WalletPK.Gateway.Webhooks;
using WalletPK.Observability;
using WalletPK.Web;

// WalletPK API Gateway: the single entry point for the web app, mobile clients and provider webhooks.
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("gateway");
builder.Services.AddWalletPkWeb();
builder.Services.AddGatewayAuthentication();
builder.Services.AddGatewayRateLimiting();
builder.Services.AddGatewayCors();
builder.Services.AddWebhookSignatureValidation();
builder.Services.AddGatewayReverseProxy(builder.Configuration);

var app = builder.Build();

// Order matters: correlation ID + error handling wrap everything, then CORS, webhook signatures,
// authentication, rate limiting (which needs the user) and authorization, then the proxy.
app.UseWalletPkWeb();
app.UseWalletPkObservability();
app.UseCors(GatewayCorsExtensions.PolicyName);
app.UseWebhookSignatureValidation();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGatewayInfo();
app.MapReverseProxy();

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
