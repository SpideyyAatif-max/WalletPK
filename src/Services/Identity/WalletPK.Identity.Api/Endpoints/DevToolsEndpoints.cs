using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Users;
using WalletPK.Identity.Infrastructure.Sms;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Identity.Api.Endpoints;

/// <summary>
/// Development-only helpers for clicking through the web app without a phone (M4.7). Not mapped in
/// any other environment, so they answer 404 there.
/// <list type="bullet">
/// <item>GET /api/identity/dev/sms?phoneNumber=   the last text messages "sent" to a number, newest first (OTPs included)</item>
/// <item>PUT /api/identity/dev/role {"role":"ops"}  makes the signed-in user customer, merchant, agent, ops, compliance, finance
/// or admin, to try the operations portal; takes effect with the next access token (refresh)</item>
/// </list>
/// </summary>
public static class DevToolsEndpoints
{
    public static IEndpointRouteBuilder MapDevToolsEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        var group = app.MapGroup("/api/identity/dev").WithTags("Development only");

        group.MapGet("/sms", (string? phoneNumber, DevSmsInbox inbox) =>
            PhoneNumber.Create(phoneNumber).ToHttpResult(phone => TypedResults.Ok(inbox.For(phone.Value))));

        group.MapPut("/role", async Task<IResult> (
            DevRoleRequest request,
            ICurrentUser currentUser,
            IUserRepository users,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var role = Enum.GetValues<UserRole>().Cast<UserRole?>()
                .FirstOrDefault(candidate => candidate!.Value.ToClaimValue() == request.Role?.Trim().ToLowerInvariant());
            if (role is null)
            {
                return Error.Validation("identity.unknown_role", "Role must be customer, merchant, agent, ops, compliance, finance or admin.").ToProblem();
            }

            var user = await users.GetByIdAsync(currentUser.GetRequiredId(), cancellationToken);
            if (user is null)
            {
                return Error.NotFound("identity.user_not_found", "User not found.").ToProblem();
            }

            user.AssignRole(role.Value, timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(new { role = role.Value.ToClaimValue() });
        }).RequireAuthorization();

        return app;
    }
}

/// <param name="Role">customer, merchant, agent, ops, compliance, finance or admin.</param>
public sealed record DevRoleRequest(string? Role);
