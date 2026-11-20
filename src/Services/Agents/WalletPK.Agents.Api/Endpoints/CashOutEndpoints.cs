using WalletPK.Agents.Application;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Agents.Api.Endpoints;

/// <param name="AgentCode">The agent's code, shown in the shop ("WPKA123456").</param>
/// <param name="AmountPaisa">Cash wanted, in whole rupees (Rs. 100 to Rs. 50,000). The fee and its tax come on top.</param>
public sealed record CashOutRequestBody(string? AgentCode, long? AmountPaisa);

/// <summary>
/// A customer's cash-out at an agent (M9.3, access token required):
/// <list type="bullet">
/// <item>POST /api/agents/cash-outs                   ask for cash at an agent: 201 with a six-digit code (shown once)</item>
/// <item>GET  /api/agents/cash-outs/current           my newest request and whether the agent has paid it out</item>
/// <item>POST /api/agents/cash-outs/{id}/cancel       cancel an open request</item>
/// </list>
/// </summary>
public static class CashOutEndpoints
{
    public static IEndpointRouteBuilder MapCashOutEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/agents/cash-outs").WithTags("Cash-out").RequireAuthorization();

        group.MapPost("/", async (CashOutRequestBody request, ICurrentUser currentUser, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.RequestCashOutAsync(AgentEndpoints.Caller(currentUser), request.AgentCode, request.AmountPaisa ?? 0, cancellationToken))
                .ToHttpResult(created => TypedResults.Created("/api/agents/cash-outs/current", created)));

        group.MapGet("/current", async (ICurrentUser currentUser, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.CurrentCashOutAsync(currentUser.GetRequiredId().Value, cancellationToken)).ToHttpResult());

        group.MapPost("/{id:guid}/cancel", async (Guid id, ICurrentUser currentUser, AgentService agents, CancellationToken cancellationToken) =>
            (await agents.CancelCashOutAsync(currentUser.GetRequiredId().Value, id, cancellationToken)).ToHttpResult());

        return endpoints;
    }
}
