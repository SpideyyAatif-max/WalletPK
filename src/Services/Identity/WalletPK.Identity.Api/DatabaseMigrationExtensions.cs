using Microsoft.EntityFrameworkCore;
using WalletPK.Identity.Infrastructure.Persistence;

namespace WalletPK.Identity.Api;

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
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await dbContext.Database.MigrateAsync();
    }
}
