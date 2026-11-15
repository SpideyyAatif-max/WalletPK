using System.Security.Cryptography;
using System.Text;
using WalletPK.Common.Results;
using WalletPK.Web.Errors;

namespace WalletPK.MockProviders.Security;

/// <summary>
/// Like real providers, the mocks only answer known callers: WalletPK services send X-Api-Key; the
/// control API (/admin) wants X-Admin-Key (used by tests and, from M13, the Simulation service).
/// </summary>
public static class ApiKeys
{
    public const string ClientHeader = "X-Api-Key";
    public const string AdminHeader = "X-Admin-Key";

    public static RouteGroupBuilder RequireClientKey(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<MockProvidersOptions>();
            var presented = context.HttpContext.Request.Headers[ClientHeader].ToString();
            return options.ClientApiKeys.Any(key => Matches(key, presented))
                ? await next(context)
                : Error.Unauthorized("provider.unauthorized", $"Send a valid {ClientHeader}.").ToProblem();
        });

    public static RouteGroupBuilder RequireAdminKey(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<MockProvidersOptions>();
            return Matches(options.AdminApiKey, context.HttpContext.Request.Headers[AdminHeader].ToString())
                ? await next(context)
                : Error.Unauthorized("provider.unauthorized", $"Send a valid {AdminHeader}.").ToProblem();
        });

    private static bool Matches(string expected, string presented) =>
        expected.Length > 0
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
}
