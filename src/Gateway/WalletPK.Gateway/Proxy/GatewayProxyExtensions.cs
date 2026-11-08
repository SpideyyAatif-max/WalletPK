using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using WalletPK.Web.Correlation;
using WalletPK.Web.Identity;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace WalletPK.Gateway.Proxy;

public static class GatewayProxyExtensions
{
    /// <summary>
    /// YARP reverse proxy with routes and clusters from the "ReverseProxy" configuration section,
    /// plus a transform that forwards the caller's identity and correlation ID to services.
    /// </summary>
    public static IServiceCollection AddGatewayReverseProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddReverseProxy()
            .LoadFromConfig(configuration.GetSection("ReverseProxy"))
            .AddTransforms(transforms => transforms.AddRequestTransform(ForwardIdentityAndCorrelation));

        return services;
    }

    /// <summary>A small landing endpoint listing the gateway's routes (handy for demos and debugging).</summary>
    public static IEndpointRouteBuilder MapGatewayInfo(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", GetInfo).AllowAnonymous().ExcludeFromDescription();
        return endpoints;
    }

    internal static ValueTask ForwardIdentityAndCorrelation(RequestTransformContext context)
    {
        var headers = context.ProxyRequest.Headers;

        // Never trust identity headers sent by the client: remove them, then add the verified values.
        foreach (var header in IdentityHeaders.All)
        {
            headers.Remove(header);
        }

        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            AddIfPresent(headers, IdentityHeaders.UserId, user.FindFirstValue(WalletPkClaimTypes.Subject));
            AddIfPresent(headers, IdentityHeaders.DeviceId, user.FindFirstValue(WalletPkClaimTypes.DeviceId));
            AddIfPresent(headers, IdentityHeaders.KycTier, user.FindFirstValue(WalletPkClaimTypes.KycTier));

            var roles = string.Join(',', user.FindAll(WalletPkClaimTypes.Role).Select(claim => claim.Value));
            AddIfPresent(headers, IdentityHeaders.Roles, roles);
        }

        // The correlation ID may have been created here at the gateway; services must receive the same one.
        var correlationId = CorrelationContext.Current;
        if (correlationId is not null)
        {
            headers.Remove(CorrelationIdMiddleware.HeaderName);
            headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlationId);
        }

        return ValueTask.CompletedTask;
    }

    private static void AddIfPresent(System.Net.Http.Headers.HttpRequestHeaders headers, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            headers.TryAddWithoutValidation(name, value);
        }
    }

    private static Ok<GatewayInfo> GetInfo(IHostEnvironment environment, IProxyConfigProvider proxyConfig) =>
        TypedResults.Ok(new GatewayInfo(
            "WalletPK API Gateway",
            environment.EnvironmentName,
            proxyConfig.GetConfig().Routes
                .Select(route => new GatewayRouteInfo(route.RouteId, route.Match.Path ?? string.Empty, route.ClusterId ?? string.Empty, route.AuthorizationPolicy ?? "none"))
                .OrderBy(route => route.Path, StringComparer.Ordinal)
                .ToArray()));
}

public sealed record GatewayInfo(string Service, string Environment, IReadOnlyList<GatewayRouteInfo> Routes);

public sealed record GatewayRouteInfo(string RouteId, string Path, string Cluster, string AuthorizationPolicy);
