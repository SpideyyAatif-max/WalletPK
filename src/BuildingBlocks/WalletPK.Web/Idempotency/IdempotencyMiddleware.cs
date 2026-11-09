using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using WalletPK.Common.Results;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Web.Idempotency;

/// <summary>Marks an endpoint as requiring an <c>Idempotency-Key</c> header (see <see cref="IdempotencyMiddleware"/>).</summary>
public sealed class RequiresIdempotencyKeyMetadata;

public static class IdempotencyExtensions
{
    /// <summary>The endpoint needs an Idempotency-Key; repeated requests with the same key get the first response.</summary>
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new RequiresIdempotencyKeyMetadata());
    }

    /// <summary>Adds the idempotency middleware. Place it after authentication/authorization (keys are per user).</summary>
    public static IApplicationBuilder UseWalletPkIdempotency(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<IdempotencyMiddleware>();
    }
}

/// <summary>
/// Makes money-moving requests safe to retry (M3.7). A phone on a weak connection may send the same
/// "send Rs 500" twice; with the same <c>Idempotency-Key</c> header, the second gets the first one's
/// response (marked <c>Idempotent-Replayed: true</c>) and nothing is paid twice.
/// <list type="bullet">
/// <item>Missing or malformed key → 400.</item>
/// <item>Same key, same body, first request still running → 409 (try again shortly).</item>
/// <item>Same key, different body → 422 (a client bug: keys must not be reused).</item>
/// <item>A 5xx response is not saved, so the client can genuinely retry.</item>
/// </list>
/// Keys are scoped per user and route, so two users can never collide.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";
    public const int MaxBodyBytes = 64 * 1024;

    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store, ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(currentUser);

        if (context.GetEndpoint()?.Metadata.GetMetadata<RequiresIdempotencyKeyMetadata>() is null)
        {
            await next(context);
            return;
        }

        var clientKey = context.Request.Headers[HeaderName].ToString();
        if (!IsValidKey(clientKey))
        {
            await WriteErrorAsync(context, Error.Validation(
                "idempotency.key_required",
                $"Send an {HeaderName} header (8-64 letters, digits, '-' or '_'), the same value for every retry of this request."));
            return;
        }

        var requestHash = await HashBodyAsync(context.Request, context.RequestAborted);
        var key = $"{currentUser.Id?.ToString() ?? "anonymous"}:{context.Request.Method}:{context.Request.Path.Value?.ToUpperInvariant()}:{clientKey}";

        var begin = await store.TryBeginAsync(key, requestHash, context.RequestAborted);
        switch (begin.Status)
        {
            case IdempotencyStatus.Completed:
                await ReplayAsync(context, begin.Response!);
                return;
            case IdempotencyStatus.InProgress:
                await WriteErrorAsync(context, Error.Conflict(
                    "idempotency.in_progress", "The first request with this Idempotency-Key is still being processed. Try again shortly."));
                return;
            case IdempotencyStatus.Mismatch:
                await WriteErrorAsync(context, Error.BusinessRule(
                    "idempotency.key_reused", "This Idempotency-Key was already used for a different request."));
                return;
        }

        // Started: run the endpoint while recording what it writes.
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        catch
        {
            context.Response.Body = originalBody;
            await store.AbandonAsync(key, CancellationToken.None);
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted);

        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            await store.AbandonAsync(key, CancellationToken.None);
            return;
        }

        await store.CompleteAsync(
            key,
            new StoredResponse(context.Response.StatusCode, context.Response.ContentType, context.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null, buffer.ToArray()),
            CancellationToken.None);
    }

    internal static bool IsValidKey(string key) =>
        key.Length is >= 8 and <= 64 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static async Task<string> HashBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering(bufferThreshold: 32 * 1024, bufferLimit: MaxBodyBytes);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            sha.AppendData(chunk, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static async Task ReplayAsync(HttpContext context, StoredResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        if (response.ContentType is not null)
        {
            context.Response.ContentType = response.ContentType;
        }

        if (response.Location is not null)
        {
            context.Response.Headers.Location = response.Location;
        }

        context.Response.Headers[ReplayedHeaderName] = "true";
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }

    private static Task WriteErrorAsync(HttpContext context, Error error) =>
        error.ToProblem().ExecuteAsync(context);
}
