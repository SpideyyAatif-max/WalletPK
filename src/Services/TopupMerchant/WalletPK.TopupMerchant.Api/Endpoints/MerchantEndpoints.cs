using Microsoft.AspNetCore.Mvc;
using WalletPK.Common.Correlation;
using WalletPK.Common.Results;
using WalletPK.Common.Time;
using WalletPK.TopupMerchant.Application.Merchants;
using WalletPK.TopupMerchant.Domain;
using WalletPK.TopupMerchant.Infrastructure.Settlement;
using WalletPK.Web.Authentication;
using WalletPK.Web.Errors;
using WalletPK.Web.Idempotency;
using WalletPK.Web.Identity;

namespace WalletPK.TopupMerchant.Api.Endpoints;

/// <param name="Name">The business name customers see.</param>
/// <param name="Category">"RETAIL", "KIRYANA", "FOOD", "FUEL" or "SERVICES" (sets the fee plan).</param>
/// <param name="City">Optional.</param>
public sealed record RegisterMerchantRequest(string? Name, string? Category, string? City);

/// <param name="AmountPaisa">Exactly what the customer pays.</param>
/// <param name="Reference">The merchant's own reference ("INV-42").</param>
/// <param name="ExpiresInSeconds">60-900 (300 when not given).</param>
public sealed record CreateQrRequest(long? AmountPaisa, string? Reference, int? ExpiresInSeconds);

/// <param name="Payload">The scanned QR text ("WPKQR1....").</param>
public sealed record QrPayloadRequest(string? Payload);

/// <summary>
/// Merchants and QR payments (M8.2-M8.5, access token required):
/// <list type="bullet">
/// <item>GET  /api/merchants/fee-plans               fee plans and business categories</item>
/// <item>POST /api/merchants                         register my business (its wallet is opened by the Ledger)</item>
/// <item>GET  /api/merchants/me                      my merchant account, with today's unsettled sales</item>
/// <item>POST /api/merchants/me/qr                   a signed QR code for an amount</item>
/// <item>GET  /api/merchants/me/qr/{id}              whether it has been paid (the till polls this)</item>
/// <item>GET  /api/merchants/me/payments             payments to me</item>
/// <item>GET  /api/merchants/me/settlements          my nightly settlements</item>
/// <item>POST /api/merchants/qr/preview              what a scanned code asks for</item>
/// <item>POST /api/merchants/qr/pay                  pay it (Idempotency-Key): 202, follow it at /api/merchants/payments/{id}</item>
/// <item>GET  /api/merchants/payments[/{id}]         my QR payments (as a payer) / one</item>
/// </list>
/// </summary>
public static class MerchantEndpoints
{
    public static IEndpointRouteBuilder MapMerchantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/merchants").WithTags("Merchants").RequireAuthorization();

        group.MapGet("/fee-plans", () => TypedResults.Ok(new { Plans = FeePlans.All, FeePlans.Categories, FeePlan.TaxBasisPoints }));

        group.MapPost("/", async (RegisterMerchantRequest request, ICurrentUser currentUser, MerchantService merchants, CancellationToken cancellationToken) =>
            (await merchants.RegisterAsync(currentUser.GetRequiredId().Value, request.Name, request.Category, request.City, CorrelationContext.Current, cancellationToken))
                .ToHttpResult(merchant => TypedResults.Created("/api/merchants/me", merchant)));

        group.MapGet("/me", async (ICurrentUser currentUser, MerchantService merchants, CancellationToken cancellationToken) =>
            (await merchants.MeAsync(currentUser.GetRequiredId().Value, cancellationToken)).ToHttpResult());

        group.MapPost("/me/qr", async (CreateQrRequest request, ICurrentUser currentUser, MerchantService merchants, CancellationToken cancellationToken) =>
            (await merchants.CreateQrAsync(currentUser.GetRequiredId().Value, request.AmountPaisa ?? 0, request.Reference, request.ExpiresInSeconds, cancellationToken))
                .ToHttpResult(qr => TypedResults.Created($"/api/merchants/me/qr/{qr.Id}", qr)));

        group.MapGet("/me/qr/{id:guid}", async (Guid id, ICurrentUser currentUser, MerchantService merchants, CancellationToken cancellationToken) =>
            (await merchants.QrAsync(currentUser.GetRequiredId().Value, id, cancellationToken)).ToHttpResult());

        group.MapGet("/me/payments", async (int? limit, ICurrentUser currentUser, MerchantService merchants, CancellationToken cancellationToken) =>
            (await merchants.PaymentsAsync(currentUser.GetRequiredId().Value, limit, cancellationToken)).ToHttpResult());

        group.MapGet("/me/settlements", async (int? limit, ICurrentUser currentUser, MerchantService merchants, CancellationToken cancellationToken) =>
            (await merchants.SettlementsAsync(currentUser.GetRequiredId().Value, limit, cancellationToken)).ToHttpResult());

        group.MapPost("/qr/preview", async (QrPayloadRequest request, ICurrentUser currentUser, MerchantService merchants, CancellationToken cancellationToken) =>
            (await merchants.PreviewAsync(currentUser.GetRequiredId().Value, request.Payload, cancellationToken)).ToHttpResult());

        group.MapPost("/qr/pay", async (
            QrPayloadRequest request,
            [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string? idempotencyKey,
            ICurrentUser currentUser,
            MerchantService merchants,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            {
                return Error.Validation("idempotency.key_required", $"Send a unique {IdempotencyMiddleware.HeaderName} header (at most 100 characters) with every payment.").ToProblem();
            }

            var result = await merchants.PayAsync(
                currentUser.GetRequiredId().Value, currentUser.PhoneNumber, currentUser.DeviceId, idempotencyKey, request.Payload, CorrelationContext.Current, cancellationToken);
            return result.ToHttpResult(payment => TypedResults.Accepted($"/api/merchants/payments/{payment.Id}", payment));
        });

        group.MapGet("/payments", async (int? limit, ICurrentUser currentUser, MerchantService merchants, CancellationToken cancellationToken) =>
            TypedResults.Ok(await merchants.PaidByAsync(currentUser.GetRequiredId().Value, limit, cancellationToken)));

        group.MapGet("/payments/{id:guid}", async (Guid id, ICurrentUser currentUser, MerchantService merchants, CancellationToken cancellationToken) =>
            (await merchants.PaymentAsync(currentUser.GetRequiredId().Value, id, cancellationToken)).ToHttpResult());

        // ---- Operations: run a settlement now (the job runs it nightly by itself) ----
        var ops = endpoints.MapGroup("/api/merchants/ops").WithTags("Merchant operations").RequireAuthorization(WalletPkPolicies.Operations);

        ops.MapPost("/settlements/run", async (DateOnly? date, MerchantSettlementService settlement, TimeProvider timeProvider, CancellationToken cancellationToken) =>
            TypedResults.Ok(await settlement.RunAsync(date ?? timeProvider.GetBusinessDate().AddDays(-1), cancellationToken)));

        return endpoints;
    }
}
