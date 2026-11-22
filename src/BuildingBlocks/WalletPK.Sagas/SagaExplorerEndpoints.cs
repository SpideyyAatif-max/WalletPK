using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace WalletPK.Sagas;

/// <summary>
/// The Saga explorer's HTTP API (M14.B5), mapped by each service that runs Sagas under its own prefix:
/// <list type="bullet">
/// <item>GET {prefix}/ops/sagas?type=&amp;state=&amp;active=&amp;stuckForMinutes=&amp;userId=&amp;correlationId=&amp;before=&amp;limit=</item>
/// <item>GET {prefix}/ops/sagas/counts?finishedWithinHours=24   what is where, oldest first to look at</item>
/// <item>GET {prefix}/ops/sagas/{id}                             data and timeline, each step with its trace ID</item>
/// </list>
/// </summary>
public static class SagaExplorerEndpoints
{
    public static RouteGroupBuilder MapSagaExplorer(this IEndpointRouteBuilder endpoints, string prefix, string policy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);

        var group = endpoints.MapGroup($"{prefix.TrimEnd('/')}/ops/sagas").WithTags("Saga explorer").RequireAuthorization(policy);

        group.MapGet("/", async (
            string? type,
            string? state,
            bool? active,
            int? stuckForMinutes,
            Guid? userId,
            string? correlationId,
            DateTimeOffset? before,
            int? limit,
            ISagaExplorer explorer,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await explorer.SearchAsync(new SagaSearch(type, state, active, stuckForMinutes, userId, correlationId, before, limit), cancellationToken)));

        group.MapGet("/counts", async (int? finishedWithinHours, ISagaExplorer explorer, CancellationToken cancellationToken) =>
            TypedResults.Ok(await explorer.CountsAsync(TimeSpan.FromHours(Math.Clamp(finishedWithinHours ?? 24, 0, 24 * 31)), cancellationToken)));

        group.MapGet("/{id:guid}", async Task<Results<Ok<SagaDetail>, ProblemHttpResult>> (Guid id, ISagaExplorer explorer, CancellationToken cancellationToken) =>
            await explorer.GetAsync(id, cancellationToken) is { } saga
                ? TypedResults.Ok(saga)
                : TypedResults.Problem(
                    detail: "No such Saga in this service.",
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Resource not found",
                    extensions: new Dictionary<string, object?> { ["code"] = "saga.not_found" }));

        return group;
    }
}
