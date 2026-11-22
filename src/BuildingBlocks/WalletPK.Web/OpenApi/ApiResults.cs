using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace WalletPK.Web.OpenApi;

/// <summary>
/// What <c>Result&lt;T&gt;.ToHttpResult()</c> returns: 200 with the value, or ProblemDetails. It describes
/// itself to OpenAPI (like ASP.NET's own <c>Results&lt;...&gt;</c>), so every endpoint that returns it
/// documents its response type without annotations, and the web app's client can be generated from that.
/// </summary>
public sealed class ApiResult<T> : IResult, IEndpointMetadataProvider
{
    private readonly IResult _inner;

    internal ApiResult(IResult inner) => _inner = inner;

    public Task ExecuteAsync(HttpContext httpContext) => _inner.ExecuteAsync(httpContext);

    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status200OK, typeof(T), ["application/json"]));
        ApiProblems.Add(builder);
    }
}

/// <summary>What <c>Result.ToHttpResult()</c> returns: 204, or ProblemDetails; documented like <see cref="ApiResult{T}"/>.</summary>
public sealed class ApiResult : IResult, IEndpointMetadataProvider
{
    private readonly IResult _inner;

    internal ApiResult(IResult inner) => _inner = inner;

    public Task ExecuteAsync(HttpContext httpContext) => _inner.ExecuteAsync(httpContext);

    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void), []));
        ApiProblems.Add(builder);
    }
}

internal static class ApiProblems
{
    /// <summary>The failures a WalletPK result can become (see <c>ErrorType</c>), all RFC 9457 ProblemDetails with a "code".</summary>
    public static void Add(EndpointBuilder builder)
    {
        foreach (var status in new[] { StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity })
        {
            builder.Metadata.Add(new ProducesResponseTypeMetadata(status, typeof(ProblemDetails), ["application/problem+json"]));
        }
    }
}
