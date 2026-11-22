using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using WalletPK.Common.Results;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;
using WalletPK.Web.Idempotency;

namespace WalletPK.Web.Security;

/// <summary>"MpinConfirmation" settings.</summary>
public sealed class MpinConfirmationOptions
{
    public const string SectionName = "MpinConfirmation";

    /// <summary>Whether money-moving endpoints insist on an MPIN confirmation (on outside tests).</summary>
    public bool Required { get; set; } = true;
}

/// <summary>
/// MPIN confirmation of a payment (proposal 12.1: "a confirmation screen and MPIN"). The app asks Identity
/// (<c>POST /api/identity/mpin/confirm</c> with the MPIN and the payment's Idempotency-Key); Identity checks the
/// MPIN (wrong ones count towards the lockout) and returns a short-lived token signed with its access-token key:
/// <code>{ "iss": "walletpk-identity", "aud": "walletpk-confirmation", "sub": "…", "device_id": "…", "binding": "&lt;key&gt;", "exp": … }</code>
/// The app sends it as <c>X-Mpin-Confirmation</c> with the payment. Being bound to the Idempotency-Key it
/// confirms exactly one payment; its audience keeps it from ever passing as an access token.
/// </summary>
public static class MpinConfirmation
{
    public const string HeaderName = "X-Mpin-Confirmation";

    public const string Audience = "walletpk-confirmation";

    /// <summary>What the confirmation is for: the payment's Idempotency-Key, or "money-request:{id}".</summary>
    public const string BindingClaim = "binding";

    public static readonly Error Required = Error.Forbidden("auth.mpin_confirmation_required", "Confirm this payment with your MPIN.");

    /// <summary>
    /// Refuses the request (403, <c>auth.mpin_confirmation_required</c>) unless it carries a valid MPIN
    /// confirmation from this user and device for <paramref name="bindingOf"/> (default: the Idempotency-Key).
    /// </summary>
    public static RouteHandlerBuilder RequireMpinConfirmation(this RouteHandlerBuilder builder, Func<HttpContext, string?>? bindingOf = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var binding = bindingOf ?? (context => context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString());
        return builder
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                var options = http.RequestServices.GetRequiredService<IOptions<MpinConfirmationOptions>>().Value;
                if (!options.Required)
                {
                    return await next(context);
                }

                var validator = http.RequestServices.GetRequiredService<MpinConfirmationValidator>();
                var currentUser = http.RequestServices.GetRequiredService<ICurrentUser>();
                var confirmed = await validator.IsValidAsync(
                    http.Request.Headers[HeaderName].ToString(), currentUser, binding(http), http.RequestAborted);
                return confirmed ? await next(context) : Required.ToProblem();
            })
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }
}

/// <summary>Checks MPIN confirmations with the keys and issuer the access tokens are checked with.</summary>
public sealed class MpinConfirmationValidator(IOptionsMonitor<JwtBearerOptions> jwtBearer)
{
    private readonly JsonWebTokenHandler _handler = new();

    public async Task<bool> IsValidAsync(string? token, ICurrentUser currentUser, string? binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(binding) || currentUser.Id is not { } userId)
        {
            return false;
        }

        var options = jwtBearer.Get(JwtBearerDefaults.AuthenticationScheme);
        var parameters = options.TokenValidationParameters.Clone();
        parameters.ValidAudience = MpinConfirmation.Audience;
        parameters.ValidAudiences = null;
        if (options.ConfigurationManager is { } manager)
        {
            var configuration = await manager.GetConfigurationAsync(cancellationToken);
            parameters.IssuerSigningKeys = [.. parameters.IssuerSigningKeys ?? [], .. configuration.SigningKeys];
        }

        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
        {
            return false;
        }

        string? Claim(string type) => result.Claims.TryGetValue(type, out var value) ? value?.ToString() : null;
        return Guid.TryParse(Claim(WalletPkClaimTypes.Subject), out var subject) && subject == userId.Value
            && string.Equals(Claim(WalletPkClaimTypes.DeviceId), currentUser.DeviceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Claim(MpinConfirmation.BindingClaim), binding, StringComparison.Ordinal);
    }
}
