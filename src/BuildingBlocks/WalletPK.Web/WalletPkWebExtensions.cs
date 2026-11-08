using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Web.Correlation;
using WalletPK.Web.Errors;
using WalletPK.Web.Health;
using WalletPK.Web.Identity;

namespace WalletPK.Web;

/// <summary>
/// One-line setup shared by every WalletPK API:
/// <code>
/// builder.Services.AddWalletPkWeb();
/// var app = builder.Build();
/// app.UseWalletPkWeb();
/// </code>
/// </summary>
public static class WalletPkWebExtensions
{
    public static IServiceCollection AddWalletPkWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        services.AddTransient<CorrelationIdDelegatingHandler>();

        services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsEnricher.Enrich);
        services.AddExceptionHandler<GlobalExceptionHandler>();

        // Enums travel as readable strings ("BusinessRule"), not numbers.
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddWalletPkHealthChecks();

        return services;
    }

    public static WebApplication UseWalletPkWeb(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Order matters: the correlation ID wraps everything, so even error responses carry it.
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.MapWalletPkHealthChecks();

        return app;
    }
}
