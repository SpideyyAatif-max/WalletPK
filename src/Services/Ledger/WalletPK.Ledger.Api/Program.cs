using WalletPK.Ledger.Api;
using WalletPK.Ledger.Infrastructure;
using WalletPK.Observability;
using WalletPK.Web;
using WalletPK.Web.Idempotency;

// WalletPK Wallet & Ledger service.
var builder = WebApplication.CreateBuilder(args);

builder.AddWalletPkObservability("ledger");
builder.Services.AddWalletPkWeb();
builder.Services.AddLedgerServices();

var app = builder.Build();

app.UseWalletPkWeb();
app.UseWalletPkObservability();

// Replays the first response for a repeated Idempotency-Key on endpoints marked RequireIdempotencyKey() (M3.7).
app.UseWalletPkIdempotency();

await app.MigrateDatabaseIfConfiguredAsync();

// Wallet endpoints (balance, transfers, history) arrive in M3.8.

await app.RunAsync();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
