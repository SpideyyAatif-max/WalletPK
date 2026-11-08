using WalletPK.Identity.Application.Authentication;
using WalletPK.Web.Errors;

namespace WalletPK.Identity.Api.Endpoints;

/// <summary>
/// Login and session endpoints, reached through the gateway at /api/identity/auth/... (no token needed):
/// <list type="bullet">
/// <item>POST /login                  phone + MPIN + device → 200 tokens (trusted device) or 202 OTP challenge (new device)</item>
/// <item>POST /login/verify-device    challenge + OTP       → 200 tokens; the device becomes trusted</item>
/// <item>POST /refresh                refresh token         → 200 new tokens (the old refresh token is used up)</item>
/// <item>POST /logout                 refresh token         → 204; the session ends</item>
/// </list>
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/identity/auth").WithTags("Authentication");

        group.MapPost("/login", async (LoginRequest request, LoginHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(
                new LoginCommand(request.PhoneNumber, request.Mpin, request.InstallationId, request.DeviceName),
                cancellationToken);

            return result.ToHttpResult(outcome => outcome.Tokens is { } tokens
                ? (IResult)TypedResults.Ok(tokens)
                : TypedResults.Accepted((string?)null, outcome.Challenge));
        });

        group.MapPost("/login/verify-device", async (VerifyDeviceRequest request, VerifyDeviceHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(new VerifyDeviceCommand(request.ChallengeId, request.Code), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/refresh", async (RefreshRequest request, RefreshSessionHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(new RefreshSessionCommand(request.RefreshToken), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/logout", async (RefreshRequest request, LogoutHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(new LogoutCommand(request.RefreshToken), cancellationToken);
            return result.ToHttpResult();
        });

        return endpoints;
    }
}

/// <param name="PhoneNumber">Any common format, for example "0300 1234567".</param>
/// <param name="Mpin">The 5-digit MPIN.</param>
/// <param name="InstallationId">Random ID the app creates once per installation (a UUID is fine).</param>
/// <param name="DeviceName">Shown in the device list, for example "Samsung Galaxy A54".</param>
public sealed record LoginRequest(string? PhoneNumber, string? Mpin, string? InstallationId, string? DeviceName);

public sealed record VerifyDeviceRequest(string? ChallengeId, string? Code);

public sealed record RefreshRequest(string? RefreshToken);
