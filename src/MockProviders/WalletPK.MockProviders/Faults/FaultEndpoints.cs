using WalletPK.Common.Results;
using WalletPK.MockProviders.Security;
using WalletPK.Web.Errors;

namespace WalletPK.MockProviders.Faults;

/// <summary>
/// Runtime fault control (M5.1), used by tests, by hand, and from M13 by the Simulation service.
/// <list type="bullet">
/// <item>GET    /admin/faults                every provider and the faults in force</item>
/// <item>PUT    /admin/faults/{provider}     set faults, e.g. {"timeoutRate": 1} for "biller:KE"</item>
/// <item>DELETE /admin/faults/{provider}     back to normal</item>
/// <item>DELETE /admin/faults                everything back to normal</item>
/// </list>
/// </summary>
public static class FaultEndpoints
{
    public static IEndpointRouteBuilder MapFaultEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/faults").WithTags("Control: faults").RequireAdminKey();

        group.MapGet("/", (FaultRegistry registry) =>
        {
            var active = registry.All().ToDictionary(faults => faults.Provider, StringComparer.Ordinal);
            return TypedResults.Ok(Providers.All.Select(provider => new
            {
                provider,
                faults = active.GetValueOrDefault(provider),
            }));
        });

        group.MapPut("/{provider}", (string provider, FaultSettings settings, FaultRegistry registry) =>
        {
            if (!Providers.IsKnown(provider))
            {
                return UnknownProvider(provider);
            }

            return settings.Problem() is { } problem
                ? Error.Validation("faults.invalid", problem).ToProblem()
                : TypedResults.Ok(registry.Set(provider, settings));
        });

        group.MapDelete("/{provider}", (string provider, FaultRegistry registry) =>
        {
            if (!Providers.IsKnown(provider))
            {
                return UnknownProvider(provider);
            }

            registry.Clear(provider);
            return TypedResults.NoContent();
        });

        group.MapDelete("/", (FaultRegistry registry) =>
        {
            registry.ClearAll();
            return TypedResults.NoContent();
        });

        return endpoints;
    }

    private static IResult UnknownProvider(string provider) =>
        Error.NotFound("faults.unknown_provider", $"No provider '{provider}'. Known: {string.Join(", ", Providers.All)}.").ToProblem();
}
