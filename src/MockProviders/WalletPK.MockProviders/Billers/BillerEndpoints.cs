using System.Text.Json;
using Npgsql;
using WalletPK.Common.Results;
using WalletPK.MockProviders.Common;
using WalletPK.MockProviders.Faults;
using WalletPK.MockProviders.Security;
using WalletPK.MockProviders.Storage;
using WalletPK.Web.Errors;

namespace WalletPK.MockProviders.Billers;

/// <param name="PaymentId">The caller's unique ID; sending it again returns the same payment.</param>
/// <param name="ConsumerNumber">Whose bill.</param>
/// <param name="AmountPaisa">Must be exactly the payable amount (no partial payments).</param>
public sealed record BillPaymentRequest(string? PaymentId, string? ConsumerNumber, long AmountPaisa);

/// <summary>A bill payment as the biller reports it.</summary>
public sealed record BillPayment(string PaymentId, string BillerReference, string Biller, string ConsumerNumber, string BillingMonth, long AmountPaisa, string Status, DateTimeOffset PaidAt);

/// <summary>
/// Mock billers (M5.3), synchronous like most Pakistani biller aggregators:
/// <list type="bullet">
/// <item>GET  /billers                                   the catalog</item>
/// <item>GET  /billers/{code}/bills/{consumerNumber}     bill inquiry</item>
/// <item>POST /billers/{code}/payments                   pay this month's bill (exact amount)</item>
/// <item>GET  /billers/{code}/payments/{paymentId}       status query: did the payment arrive?</item>
/// </list>
/// Faults: "biller:{code}". With accept-but-timeout the bill IS paid but the caller hears nothing: only
/// the status query (or the settlement file) tells.
/// </summary>
public static class BillerEndpoints
{
    public const string PaymentKind = "bill_payment";

    public static IEndpointRouteBuilder MapBillerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/billers").WithTags("Billers").RequireClientKey();

        group.MapGet("/", () => TypedResults.Ok(BillerCatalog.All));

        group.MapGet("/{code}/bills/{consumerNumber}", (string code, string consumerNumber, HttpContext context, FaultInjector faults, OperationStore store, TimeProvider clock) =>
        {
            if (BillerCatalog.ByCode(code) is not { } biller)
            {
                return Task.FromResult(UnknownBiller(code));
            }

            return faults.RunAsync(context, [Providers.Biller(biller.Code)], async () =>
            {
                if (Validate(biller, consumerNumber) is { } invalid)
                {
                    return invalid;
                }

                var today = PakistanTime.BusinessDate(clock.GetUtcNow());
                var month = $"{today:yyyy-MM}";
                var paid = BillerCatalog.PrePaid(consumerNumber)
                    || await store.FindPaidBillAsync(Providers.Biller(biller.Code), consumerNumber, month, context.RequestAborted) is not null;
                return Results.Ok(BillerCatalog.BillFor(biller, consumerNumber, today, paid));
            });
        });

        group.MapPost("/{code}/payments", (string code, BillPaymentRequest request, HttpContext context, FaultInjector faults, OperationStore store, TimeProvider clock) =>
        {
            if (BillerCatalog.ByCode(code) is not { } biller)
            {
                return Task.FromResult(UnknownBiller(code));
            }

            if (request.PaymentId is not { Length: > 0 and <= 64 } paymentId)
            {
                return Task.FromResult(MockErrors.Invalid("biller.invalid_payment_id", "paymentId is required (at most 64 characters).").ToProblem());
            }

            return faults.RunAsync(context, [Providers.Biller(biller.Code)], async () =>
            {
                var provider = Providers.Biller(biller.Code);
                var hash = Operation.HashOf(new { biller = biller.Code, request });
                if (await store.GetAsync(provider, paymentId, context.RequestAborted) is { } earlier)
                {
                    return earlier.RequestHash == hash ? Results.Ok(View(earlier)) : MockErrors.DuplicateId("paymentId").ToProblem();
                }

                var consumerNumber = request.ConsumerNumber ?? "";
                if (Validate(biller, consumerNumber) is { } invalid)
                {
                    return invalid;
                }

                if (BillerCatalog.Disconnected(consumerNumber))
                {
                    return MockErrors.Rejected("biller.connection_disconnected", "This connection is disconnected; the bill cannot be paid online.").ToProblem();
                }

                var now = clock.GetUtcNow();
                var today = PakistanTime.BusinessDate(now);
                var bill = BillerCatalog.BillFor(biller, consumerNumber, today, paid: false);
                if (BillerCatalog.PrePaid(consumerNumber) || await store.FindPaidBillAsync(provider, consumerNumber, bill.BillingMonth, context.RequestAborted) is not null)
                {
                    return Error.Conflict("biller.bill_already_paid", $"The {bill.BillingMonth} bill for this consumer is already paid.").ToProblem();
                }

                if (request.AmountPaisa != bill.PayablePaisa)
                {
                    return MockErrors.Rejected("biller.amount_mismatch", $"The payable amount today is {bill.PayablePaisa} paisa; partial or over-payments are not accepted.").ToProblem();
                }

                try
                {
                    var (stored, created) = await store.AddOrGetAsync(
                        new Operation
                        {
                            Provider = provider,
                            ClientId = paymentId,
                            Kind = PaymentKind,
                            Reference = $"{biller.Code}{now:yyMMdd}{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
                            RequestHash = hash,
                            AmountPaisa = request.AmountPaisa,
                            Counterparty = consumerNumber,
                            SettlementParty = provider,
                            Status = OperationStatus.Completed,
                            Details = JsonSerializer.Serialize(new { billingMonth = bill.BillingMonth }, JsonSerializerOptions.Web),
                            CreatedAt = now.UtcDateTime,
                            CompletedAt = now.UtcDateTime,
                        },
                        context.RequestAborted);
                    return created || stored.RequestHash == hash ? Results.Ok(View(stored)) : MockErrors.DuplicateId("paymentId").ToProblem();
                }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == "ux_operations_bill_paid")
                {
                    // Another payment for the same bill won the race.
                    return Error.Conflict("biller.bill_already_paid", $"The {bill.BillingMonth} bill for this consumer is already paid.").ToProblem();
                }
            });
        });

        group.MapGet("/{code}/payments/{paymentId}", (string code, string paymentId, HttpContext context, FaultInjector faults, OperationStore store) =>
        {
            if (BillerCatalog.ByCode(code) is not { } biller)
            {
                return Task.FromResult(UnknownBiller(code));
            }

            return faults.RunAsync(context, [Providers.Biller(biller.Code)], async () =>
                await store.GetAsync(Providers.Biller(biller.Code), paymentId, context.RequestAborted) is { } payment
                    ? Results.Ok(View(payment))
                    : Error.NotFound("biller.payment_not_found", "The biller never received a payment with this ID.").ToProblem());
        });

        return endpoints;
    }

    private sealed record BillDetails(string BillingMonth);

    private static IResult? Validate(Biller biller, string consumerNumber)
    {
        if (!BillerCatalog.IsValidConsumerNumber(biller, consumerNumber))
        {
            return MockErrors.Invalid("biller.invalid_consumer_number", $"A {biller.Name} consumer number is {biller.ConsumerNumberLength} digits.").ToProblem();
        }

        return BillerCatalog.NotFound(consumerNumber)
            ? Error.NotFound("biller.consumer_not_found", "No consumer with this number.").ToProblem()
            : null;
    }

    private static BillPayment View(Operation payment) => new(
        payment.ClientId,
        payment.Reference,
        payment.Provider["biller:".Length..],
        payment.Counterparty,
        payment.DetailsAs<BillDetails>().BillingMonth,
        payment.AmountPaisa,
        payment.Status,
        new DateTimeOffset(DateTime.SpecifyKind(payment.CompletedAt ?? payment.CreatedAt, DateTimeKind.Utc)));

    private static IResult UnknownBiller(string code) =>
        Error.NotFound("biller.unknown", $"No biller '{code}'. Billers: {string.Join(", ", BillerCatalog.All.Select(biller => biller.Code))}.").ToProblem();
}
