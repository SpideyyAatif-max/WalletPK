using System.Text.Json;
using WalletPK.Common.Results;
using WalletPK.MockProviders.Common;
using WalletPK.MockProviders.Faults;
using WalletPK.MockProviders.Security;
using WalletPK.MockProviders.Storage;
using WalletPK.Web.Errors;

namespace WalletPK.MockProviders.Telcos;

/// <param name="TopUpId">The caller's unique ID; sending it again returns the same top-up.</param>
/// <param name="Msisdn">The mobile number to top up.</param>
/// <param name="AmountPaisa">Airtime amount, or ...</param>
/// <param name="BundleCode">... a bundle to buy (then the bundle's price is charged).</param>
public sealed record TopUpRequest(string? TopUpId, string? Msisdn, long? AmountPaisa, string? BundleCode);

/// <summary>A top-up as the network reports it.</summary>
public sealed record TopUp(string TopUpId, string TelcoReference, string Telco, string Msisdn, long AmountPaisa, string? BundleCode, string Status, DateTimeOffset CompletedAt);

/// <summary>
/// Mock mobile networks (M5.4):
/// <list type="bullet">
/// <item>GET  /telcos                          networks, number prefixes and bundles</item>
/// <item>GET  /telcos/{code}/bundles           one network's bundles</item>
/// <item>POST /telcos/{code}/topups            airtime or a bundle, applied instantly</item>
/// <item>GET  /telcos/{code}/topups/{topUpId}  status query</item>
/// </list>
/// Faults: "telco:{code}".
/// </summary>
public static class TelcoEndpoints
{
    public const string TopUpKind = "topup";

    public static IEndpointRouteBuilder MapTelcoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/telcos").WithTags("Telcos").RequireClientKey();

        group.MapGet("/", () => TypedResults.Ok(TelcoCatalog.All));

        group.MapGet("/{code}/bundles", (string code) =>
            TelcoCatalog.ByCode(code) is { } telco ? Results.Ok(telco.Bundles) : UnknownTelco(code));

        group.MapPost("/{code}/topups", (string code, TopUpRequest request, HttpContext context, FaultInjector faults, OperationStore store, TimeProvider clock) =>
        {
            if (TelcoCatalog.ByCode(code) is not { } telco)
            {
                return Task.FromResult(UnknownTelco(code));
            }

            if (request.TopUpId is not { Length: > 0 and <= 64 } topUpId)
            {
                return Task.FromResult(MockErrors.Invalid("telco.invalid_topup_id", "topUpId is required (at most 64 characters).").ToProblem());
            }

            return faults.RunAsync(context, [Providers.Telco(telco.Code)], async () =>
            {
                var provider = Providers.Telco(telco.Code);
                var hash = Operation.HashOf(new { telco = telco.Code, request });
                if (await store.GetAsync(provider, topUpId, context.RequestAborted) is { } earlier)
                {
                    return earlier.RequestHash == hash ? Results.Ok(View(earlier)) : MockErrors.DuplicateId("topUpId").ToProblem();
                }

                if (Mobile.ToE164(request.Msisdn) is not { } msisdn)
                {
                    return MockErrors.Invalid("telco.invalid_msisdn", "msisdn must be a Pakistani mobile number.").ToProblem();
                }

                if (TelcoCatalog.NetworkOf(msisdn) != telco)
                {
                    return MockErrors.Rejected("telco.msisdn_not_on_network", $"{msisdn} is not a {telco.Name} number.").ToProblem();
                }

                if (TelcoCatalog.Inactive(msisdn))
                {
                    return MockErrors.Rejected("telco.msisdn_inactive", "This number is inactive.").ToProblem();
                }

                long amount;
                if (request.BundleCode is { Length: > 0 } bundleCode)
                {
                    if (telco.Bundles.FirstOrDefault(bundle => bundle.Code.Equals(bundleCode, StringComparison.OrdinalIgnoreCase)) is not { } bundle)
                    {
                        return Error.NotFound("telco.unknown_bundle", $"{telco.Name} has no bundle '{bundleCode}'.").ToProblem();
                    }

                    if (request.AmountPaisa is { } given && given != bundle.PricePaisa)
                    {
                        return MockErrors.Rejected("telco.amount_mismatch", $"{bundle.Name} costs {bundle.PricePaisa} paisa.").ToProblem();
                    }

                    amount = bundle.PricePaisa;
                }
                else if (request.AmountPaisa is { } airtime && airtime is >= TelcoCatalog.MinTopUpPaisa and <= TelcoCatalog.MaxTopUpPaisa && airtime % 100 == 0)
                {
                    amount = airtime;
                }
                else
                {
                    return MockErrors.Invalid("telco.invalid_amount", "Airtime is whole rupees from Rs. 20 to Rs. 5,000 (amountPaisa 2000-500000).").ToProblem();
                }

                var now = clock.GetUtcNow();
                var (stored, created) = await store.AddOrGetAsync(
                    new Operation
                    {
                        Provider = provider,
                        ClientId = topUpId,
                        Kind = TopUpKind,
                        Reference = $"{telco.Code}{now:yyMMdd}{Guid.NewGuid():N}"[..22].ToUpperInvariant(),
                        RequestHash = hash,
                        AmountPaisa = amount,
                        Counterparty = msisdn,
                        SettlementParty = provider,
                        Status = OperationStatus.Completed,
                        Details = JsonSerializer.Serialize(new { bundleCode = request.BundleCode }, JsonSerializerOptions.Web),
                        CreatedAt = now.UtcDateTime,
                        CompletedAt = now.UtcDateTime,
                    },
                    context.RequestAborted);
                return created || stored.RequestHash == hash ? Results.Ok(View(stored)) : MockErrors.DuplicateId("topUpId").ToProblem();
            });
        });

        group.MapGet("/{code}/topups/{topUpId}", (string code, string topUpId, HttpContext context, FaultInjector faults, OperationStore store) =>
        {
            if (TelcoCatalog.ByCode(code) is not { } telco)
            {
                return Task.FromResult(UnknownTelco(code));
            }

            return faults.RunAsync(context, [Providers.Telco(telco.Code)], async () =>
                await store.GetAsync(Providers.Telco(telco.Code), topUpId, context.RequestAborted) is { } topUp
                    ? Results.Ok(View(topUp))
                    : Error.NotFound("telco.topup_not_found", "The network never received a top-up with this ID.").ToProblem());
        });

        return endpoints;
    }

    private static TopUp View(Operation topUp) => new(
        topUp.ClientId,
        topUp.Reference,
        topUp.Provider["telco:".Length..],
        topUp.Counterparty,
        topUp.AmountPaisa,
        topUp.DetailsAs<TopUpDetails>().BundleCode,
        topUp.Status,
        new DateTimeOffset(DateTime.SpecifyKind(topUp.CompletedAt ?? topUp.CreatedAt, DateTimeKind.Utc)));

    private static IResult UnknownTelco(string code) =>
        Error.NotFound("telco.unknown", $"No network '{code}'. Networks: {string.Join(", ", TelcoCatalog.All.Select(telco => telco.Code))}.").ToProblem();
}

internal sealed record TopUpDetails(string? BundleCode);
