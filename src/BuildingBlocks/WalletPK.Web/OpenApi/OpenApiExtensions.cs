using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;

namespace WalletPK.Web.OpenApi;

/// <summary>
/// Every service publishes its OpenAPI 3.1 document at /openapi/v1.json (M14.A2, M16.4): what each endpoint
/// takes and returns, its errors as ProblemDetails, and the bearer token it needs. The web app generates its
/// typed client from these.
/// </summary>
public static class OpenApiExtensions
{
    public const string DocumentPath = "/openapi/{documentName}.json";

    public static IServiceCollection AddWalletPkOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOpenApi(options => options.AddDocumentTransformer((document, context, _) =>
        {
            var application = context.ApplicationServices.GetRequiredService<IHostEnvironment>().ApplicationName;
            document.Info = new OpenApiInfo
            {
                Title = application.Replace(".Api", "", StringComparison.Ordinal),
                Version = "v1",
                Description = "WalletPK API. Errors are RFC 9457 ProblemDetails with a stable code. Money-moving calls need an Idempotency-Key header.",
            };
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "The access token from Identity (POST /api/identity/auth/login).",
            };
            document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
            return Task.CompletedTask;
        }));
        return services;
    }

    /// <summary>Maps the document; it is public (no secrets in it) so tools can fetch it.</summary>
    public static IEndpointRouteBuilder MapWalletPkOpenApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapOpenApi(DocumentPath).AllowAnonymous();
        return endpoints;
    }
}
