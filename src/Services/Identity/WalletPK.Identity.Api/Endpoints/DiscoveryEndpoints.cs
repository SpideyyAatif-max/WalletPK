using WalletPK.Identity.Infrastructure.Security;

namespace WalletPK.Identity.Api.Endpoints;

/// <summary>
/// Standard discovery documents that let the gateway (and any service) verify access tokens without
/// sharing a secret: it reads the JWKS URL from <c>/.well-known/openid-configuration</c>, downloads
/// the public keys, caches them and refreshes them when it sees an unknown key ID.
/// </summary>
public static class DiscoveryEndpoints
{
    public const string JwksPath = "/.well-known/jwks.json";

    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/openid-configuration", (HttpRequest request, JwtSigningOptions jwt) =>
            TypedResults.Json(new Dictionary<string, object>
            {
                ["issuer"] = jwt.Issuer,
                ["jwks_uri"] = $"{request.Scheme}://{request.Host}{request.PathBase}{JwksPath}",
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["subject_types_supported"] = new[] { "public" },
                ["response_types_supported"] = new[] { "token" },
            }))
            .WithTags("Discovery");

        endpoints.MapGet(JwksPath, (HttpResponse response, SigningKeyProvider keys) =>
        {
            // Public keys change rarely; let verifiers cache them for a few minutes.
            response.Headers.CacheControl = "public, max-age=300";
            return TypedResults.Json(keys.JsonWebKeySet);
        })
            .WithTags("Discovery");

        return endpoints;
    }
}
