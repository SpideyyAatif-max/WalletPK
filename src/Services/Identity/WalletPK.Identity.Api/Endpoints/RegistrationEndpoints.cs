using WalletPK.Identity.Application.Registration;
using WalletPK.Web.Errors;

namespace WalletPK.Identity.Api.Endpoints;

/// <summary>
/// Three-step sign-up, reached through the gateway at /api/identity/auth/register/...:
/// 1. POST /            phone number  → OTP sent (202)
/// 2. POST /verify-otp  phone + OTP   → registration token (200)
/// 3. POST /set-mpin    token + MPIN  → account created (201)
/// </summary>
public static class RegistrationEndpoints
{
    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/identity/auth/register").WithTags("Registration");

        group.MapPost("/", async (StartRegistrationRequest request, StartRegistrationHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(new StartRegistrationCommand(request.PhoneNumber), cancellationToken);
            return result.ToHttpResult(challenge => TypedResults.Accepted((string?)null, challenge));
        });

        group.MapPost("/verify-otp", async (VerifyOtpRequest request, VerifyRegistrationOtpHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(new VerifyRegistrationOtpCommand(request.PhoneNumber, request.Code), cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/set-mpin", async (SetMpinRequest request, CompleteRegistrationHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(
                new CompleteRegistrationCommand(request.RegistrationToken, request.Mpin, request.ConfirmMpin),
                cancellationToken);
            return result.ToHttpResult(user => TypedResults.Created($"/api/identity/users/{user.UserId}", user));
        });

        return endpoints;
    }
}

public sealed record StartRegistrationRequest(string? PhoneNumber);

public sealed record VerifyOtpRequest(string? PhoneNumber, string? Code);

public sealed record SetMpinRequest(string? RegistrationToken, string? Mpin, string? ConfirmMpin);
