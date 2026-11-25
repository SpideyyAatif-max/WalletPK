using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json.Serialization.Metadata;
using Microsoft.OpenApi;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;

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
        services.AddOpenApi(options => options
            .AddSchemaTransformer((schema, context, _) =>
            {
                // Types with their own JSON converters: describe what the converter writes.
                var clrType = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
                if (clrType == typeof(Money))
                {
                    schema.Type = JsonSchemaType.Object;
                    schema.Required = new HashSet<string>(StringComparer.Ordinal) { "minorUnits", "currency" };
                    schema.Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                    {
                        ["minorUnits"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64", Description = "Paisa (Rs. 1 = 100)." },
                        ["currency"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "ISO 4217, for example PKR." },
                    };
                    return Task.CompletedTask;
                }

                if (clrType.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>)))
                {
                    schema.Type = JsonSchemaType.String;
                    schema.Format = "uuid";
                    return Task.CompletedTask;
                }

                // Every property the serializer always writes is required, so generated clients do not see
                // optional fields on classes (entities with setters) that are in every response.
                if (context.JsonTypeInfo.Kind == JsonTypeInfoKind.Object && schema.Properties is { Count: > 0 } properties)
                {
                    var alwaysWritten = context.JsonTypeInfo.Properties
                        .Where(property => property.ShouldSerialize is null && properties.ContainsKey(property.Name))
                        .Select(property => property.Name);
                    schema.Required = new HashSet<string>(schema.Required ?? new HashSet<string>(), StringComparer.Ordinal);
                    foreach (var name in alwaysWritten)
                    {
                        schema.Required.Add(name);
                    }
                }

                // The web JSON defaults also *read* numbers written as strings, so numbers are described as
                // "integer or string". The API always writes numbers: describe them as numbers only.
                if (schema.Type is { } type && (type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number)) && type.HasFlag(JsonSchemaType.String))
                {
                    schema.Type = type & ~JsonSchemaType.String;
                    schema.Pattern = null;
                }

                return Task.CompletedTask;
            })
            .AddDocumentTransformer((document, context, _) =>
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
