using WalletPK.Notifications.Application;
using WalletPK.Web.Authentication;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Notifications.Api.Endpoints;

/// <param name="Count">Unread notifications.</param>
public sealed record UnreadCount(int Count);

/// <summary>
/// The notification centre (M11.4), for the signed-in user:
/// <list type="bullet">
/// <item>GET  /api/notifications?before=&amp;limit=   newest first, in the user's language, with the unread count</item>
/// <item>GET  /api/notifications/unread-count</item>
/// <item>POST /api/notifications/{id}/read           returns the unread count</item>
/// <item>POST /api/notifications/read-all</item>
/// <item>GET|PUT /api/notifications/settings         language (en/ur), SMS and email on or off, email address</item>
/// <item>GET  /api/notifications/dev/messages        the SMS and emails sent to the user (Development only)</item>
/// </list>
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var group = endpoints.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization(WalletPkPolicies.Authenticated);

        group.MapGet("/", async (string? before, int? limit, ICurrentUser user, NotificationCentre centre, CancellationToken cancellationToken) =>
            (await centre.PageAsync(user.GetRequiredId().Value, before, limit, cancellationToken)).ToHttpResult());

        group.MapGet("/unread-count", async (ICurrentUser user, NotificationCentre centre, CancellationToken cancellationToken) =>
            TypedResults.Ok(new UnreadCount(await centre.UnreadCountAsync(user.GetRequiredId().Value, cancellationToken))));

        group.MapPost("/{id:guid}/read", async (Guid id, ICurrentUser user, NotificationCentre centre, CancellationToken cancellationToken) =>
            (await centre.MarkReadAsync(user.GetRequiredId().Value, id, cancellationToken)).Map(count => new UnreadCount(count)).ToHttpResult());

        group.MapPost("/read-all", async (ICurrentUser user, NotificationCentre centre, CancellationToken cancellationToken) =>
        {
            await centre.MarkAllReadAsync(user.GetRequiredId().Value, cancellationToken);
            return TypedResults.Ok(new UnreadCount(0));
        });

        group.MapGet("/settings", async (ICurrentUser user, NotificationCentre centre, CancellationToken cancellationToken) =>
            TypedResults.Ok(await centre.SettingsAsync(user.GetRequiredId().Value, user.PhoneNumber, cancellationToken)));

        group.MapPut("/settings", async (UpdateSettingsRequest request, ICurrentUser user, NotificationCentre centre, CancellationToken cancellationToken) =>
            (await centre.UpdateSettingsAsync(user.GetRequiredId().Value, user.PhoneNumber, request, cancellationToken)).ToHttpResult());

        if (environment.IsDevelopment())
        {
            group.MapGet("/dev/messages", async (ICurrentUser user, NotificationCentre centre, CancellationToken cancellationToken) =>
                TypedResults.Ok(await centre.SentMessagesAsync(user.GetRequiredId().Value, cancellationToken)))
                .WithTags("Development");
        }

        return endpoints;
    }
}
