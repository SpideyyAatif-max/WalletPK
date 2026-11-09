using Microsoft.EntityFrameworkCore;
using WalletPK.Ledger.Infrastructure.Persistence;

namespace WalletPK.Ledger.Api;

internal static class DatabaseMigrationExtensions
{
    /// <summary>
    /// Applies pending EF Core migrations at startup when "Database:MigrateOnStartup" is true
    /// (local development and tests). Production applies migrations as a separate deployment step.
    /// </summary>
    public static async Task MigrateDatabaseIfConfiguredAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Database:MigrateOnStartup", defaultValue: false))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        await dbContext.Database.MigrateAsync();
    }
}
