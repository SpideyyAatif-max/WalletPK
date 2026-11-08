using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WalletPK.Web.Health;

/// <summary>Registers and maps <c>/health/live</c> and <c>/health/ready</c> with a JSON report.</summary>
public static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    /// <summary>
    /// Adds the built-in liveness check. Services add their readiness checks to the returned builder,
    /// for example <c>.AddNpgSql(connectionString, tags: [HealthCheckTags.Ready])</c>.
    /// </summary>
    public static IHealthChecksBuilder AddWalletPkHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("The process is running."), tags: [HealthCheckTags.Live]);

    public static IEndpointRouteBuilder MapWalletPkHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthCheckTags.Live),
            ResponseWriter = WriteJsonAsync,
        }).AllowAnonymous();

        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthCheckTags.Ready),
            ResponseWriter = WriteJsonAsync,
        }).AllowAnonymous();

        return endpoints;
    }

    private static Task WriteJsonAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                description = entry.Value.Description,
                error = entry.Value.Exception?.Message,
            }),
        });
}
