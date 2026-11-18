using System.Security.Cryptography;
using System.Text;
using WalletPK.Bills.Application.Billers;
using WalletPK.Bills.Domain;
using WalletPK.Common.Results;
using WalletPK.Sagas;

namespace WalletPK.Bills.Application.Payments;

public sealed record BillTimelineStep(int Sequence, string State, string Detail, DateTimeOffset At);

public sealed record BillPaymentView(
    Guid Id,
    string Status,
    string Step,
    string Biller,
    string BillerName,
    string ConsumerNumber,
    string? CustomerName,
    long AmountPaisa,
    string? BillingMonth,
    string? BillerReference,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<BillTimelineStep>? Timeline);

/// <summary>A dead-lettered (or pending) payment, for operations.</summary>
public sealed record DeadLetterView(
    Guid Id,
    Guid UserId,
    string Step,
    string Biller,
    string ConsumerNumber,
    long AmountPaisa,
    int PayAttempts,
    string? Reason,
    DateTimeOffset? DeadLetteredAt,
    DateTimeOffset RequestedAt,
    IReadOnlyList<BillTimelineStep> Timeline);

public sealed record PaidBill(BillPaymentView Payment, bool Created);

/// <summary>Port: lists over saga_instances.</summary>
public interface IBillPaymentQueries
{
    Task<IReadOnlyList<SagaSnapshot<BillPaymentData>>> ForUserAsync(Guid userId, int limit, DateTimeOffset? before, CancellationToken cancellationToken);

    /// <summary>Payments in <paramref name="states"/>, oldest first (the operations queue).</summary>
    Task<IReadOnlyList<SagaSnapshot<BillPaymentData>>> InStatesAsync(IReadOnlyList<string> states, int limit, CancellationToken cancellationToken);
}

public static class BillErrors
{
    public static readonly Error UnknownBiller = Error.Validation("bills.unknown_biller", $"Billers: {string.Join(", ", BillerCatalog.All.Select(biller => biller.Code))}.");

    public static Error InvalidConsumerNumber(Biller biller) =>
        Error.Validation("bills.invalid_consumer_number", $"A {biller.Name} {biller.ConsumerNumberLabel.ToLowerInvariant()} is {biller.ConsumerNumberLength} digits.");

    public static readonly Error AmountOutOfRange = Error.Validation("bills.amount_out_of_range", "A bill payment is between Rs. 1 and Rs. 1,000,000.");
    public static readonly Error ConsumerNotFound = Error.NotFound("bills.consumer_not_found", "The biller has no consumer with this number.");
    public static readonly Error KeyReused = Error.Conflict("bills.idempotency_key_reused", "This Idempotency-Key was already used for a different payment.");
    public static readonly Error PaymentNotFound = Error.NotFound("bills.payment_not_found", "No such bill payment.");

    public static Error BillerUnavailable(string? detail) => Error.Unavailable("bills.biller_unavailable", $"The biller could not be reached ({detail}). Try again in a moment.");

    public static Error Refused(string? code, string? detail) => Error.Validation(code ?? "bills.refused", detail ?? "The biller refused the request.");

    public static Error NotResolvable(string state) =>
        Error.Conflict("bills.not_resolvable", $"The payment is {state}; only dead-lettered or pending-verification payments can be replayed, refunded or confirmed.");
}

/// <summary>
/// Bill payment for customers (M7.1–M7.3) and its operations queue (M7.5).
/// </summary>
public sealed class BillsService(
    ISagaEngine engine,
    ISagaReader reader,
    IBillPaymentQueries queries,
    BillPaymentSaga definition,
    IBillerGateway billers,
    IBillerAvailability availability,
    TimeProvider timeProvider)
{
    /// <summary>Dead-lettered first, then payments still waiting for the biller.</summary>
    private static readonly string[] OperationsStates = [BillPaymentStates.DeadLettered, BillPaymentStates.PendingVerification];

    public IReadOnlyList<BillerAvailability> Billers() => availability.GetAll();

    /// <summary>This month's bill (M7.2): checked locally first, then asked of the biller.</summary>
    public async Task<Result<BillInfo>> InquireAsync(string? billerCode, string? consumerNumber, CancellationToken cancellationToken)
    {
        var target = Validate(billerCode, consumerNumber);
        if (target.IsFailure)
        {
            return target.Error;
        }

        var (biller, number) = target.Value;
        var result = await billers.InquireAsync(biller, number, cancellationToken);
        return result switch
        {
            { Outcome: BillerCallOutcome.Ok, Bill: { } bill } => bill,
            { Outcome: BillerCallOutcome.NotFound } => BillErrors.ConsumerNotFound,
            { Outcome: BillerCallOutcome.Rejected } => BillErrors.Refused(result.Code, result.Detail),
            _ => BillErrors.BillerUnavailable(result.Detail),
        };
    }

    /// <summary>
    /// Accepts a payment and starts its Saga (M7.3). No biller is called here, so it is accepted even if the
    /// biller is slow; the amount is the payable amount the customer saw in the inquiry (the biller refuses
    /// any other, and the money then comes back).
    /// </summary>
    public async Task<Result<PaidBill>> PayAsync(
        Guid userId,
        string? deviceId,
        string idempotencyKey,
        string? billerCode,
        string? consumerNumber,
        long amountPaisa,
        string? customerName,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var target = Validate(billerCode, consumerNumber);
        if (target.IsFailure)
        {
            return target.Error;
        }

        if (amountPaisa is < BillRules.MinAmountPaisa or > BillRules.MaxAmountPaisa)
        {
            return BillErrors.AmountOutOfRange;
        }

        var (biller, number) = target.Value;
        var id = PaymentIdFor(userId, idempotencyKey);
        var data = new BillPaymentData
        {
            UserId = userId,
            DeviceId = deviceId,
            IdempotencyKey = idempotencyKey,
            Biller = biller.Code,
            ConsumerNumber = number,
            CustomerName = string.IsNullOrWhiteSpace(customerName) ? null : customerName.Trim()[..Math.Min(customerName.Trim().Length, 100)],
            AmountPaisa = amountPaisa,
            RequestedAt = timeProvider.GetUtcNow(),
        };

        var created = await engine.StartAsync(definition, id, data, correlationId, cancellationToken);
        var saga = await reader.FindAsync<BillPaymentData>(id, cancellationToken) ?? throw new InvalidOperationException($"Bill payment {id} vanished.");
        if (!created && (saga.Data.UserId != userId || saga.Data.Biller != biller.Code || saga.Data.ConsumerNumber != number || saga.Data.AmountPaisa != amountPaisa))
        {
            return BillErrors.KeyReused;
        }

        return new PaidBill(ToView(saga, withTimeline: true), created);
    }

    public async Task<Result<BillPaymentView>> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var saga = await reader.FindAsync<BillPaymentData>(id, cancellationToken);
        return saga is null || saga.SagaType != BillPaymentSaga.SagaName || saga.Data.UserId != userId
            ? BillErrors.PaymentNotFound
            : ToView(saga, withTimeline: true);
    }

    public async Task<IReadOnlyList<BillPaymentView>> ListAsync(Guid userId, int? limit, DateTimeOffset? before, CancellationToken cancellationToken)
    {
        var sagas = await queries.ForUserAsync(userId, Math.Clamp(limit ?? 20, 1, 100), before, cancellationToken);
        return [.. sagas.Select(saga => ToView(saga, withTimeline: false))];
    }

    // ---- Operations (M7.5) ----

    /// <summary>The queue: dead-lettered payments and those still pending verification.</summary>
    public async Task<IReadOnlyList<DeadLetterView>> DeadLettersAsync(int? limit, CancellationToken cancellationToken)
    {
        var sagas = await queries.InStatesAsync(OperationsStates, Math.Clamp(limit ?? 50, 1, 200), cancellationToken);
        var views = new List<DeadLetterView>();
        foreach (var saga in sagas)
        {
            var full = await reader.FindAsync<BillPaymentData>(saga.Id, cancellationToken) ?? saga;
            var data = full.Data;
            views.Add(new DeadLetterView(
                full.Id, data.UserId, full.State, data.Biller, data.ConsumerNumber, data.AmountPaisa, data.PayAttempts,
                data.DeadLetterReason, data.DeadLetteredAt, data.RequestedAt, Timeline(full.Steps)));
        }

        return views;
    }

    public Task<Result<BillPaymentView>> ReplayAsync(Guid id, string operatorName, CancellationToken cancellationToken) =>
        ResolveAsync(id, new ReplayBillPayment(operatorName), cancellationToken);

    public Task<Result<BillPaymentView>> RefundAsync(Guid id, string operatorName, string? reason, CancellationToken cancellationToken) =>
        ResolveAsync(id, new RefundBillPayment(operatorName, string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason.Trim()), cancellationToken);

    public Task<Result<BillPaymentView>> ConfirmAsync(Guid id, string operatorName, string? billerReference, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(billerReference)
            ? Task.FromResult<Result<BillPaymentView>>(Error.Validation("bills.reference_required", "Give the biller's reference from its settlement file."))
            : ResolveAsync(id, new ConfirmBillPayment(operatorName, billerReference.Trim()), cancellationToken);

    private async Task<Result<BillPaymentView>> ResolveAsync(Guid id, object message, CancellationToken cancellationToken)
    {
        var saga = await reader.FindAsync<BillPaymentData>(id, cancellationToken);
        if (saga is null || saga.SagaType != BillPaymentSaga.SagaName)
        {
            return BillErrors.PaymentNotFound;
        }

        if (!OperationsStates.Contains(saga.State))
        {
            return BillErrors.NotResolvable(saga.State);
        }

        await engine.HandleAsync(id, message, causationId: null, cancellationToken);
        return ToView((await reader.FindAsync<BillPaymentData>(id, cancellationToken))!, withTimeline: true);
    }

    // ---- Helpers ----

    public static Result<(Biller Biller, string ConsumerNumber)> Validate(string? billerCode, string? consumerNumber)
    {
        if (BillerCatalog.ByCode(billerCode) is not { } biller)
        {
            return BillErrors.UnknownBiller;
        }

        return BillerCatalog.NormalizeConsumerNumber(biller, consumerNumber) is { } number
            ? (biller, number)
            : BillErrors.InvalidConsumerNumber(biller);
    }

    /// <summary>The same customer and key always give the same payment (and biller payment ID).</summary>
    public static Guid PaymentIdFor(Guid userId, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"walletpk:bills:payment:{userId}:{idempotencyKey}"), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private static IReadOnlyList<BillTimelineStep> Timeline(IReadOnlyList<SagaStep> steps) =>
        [.. steps.Select(step => new BillTimelineStep(step.Sequence, step.State, step.Detail, step.At))];

    internal static BillPaymentView ToView(SagaSnapshot<BillPaymentData> saga, bool withTimeline)
    {
        var data = saga.Data;
        return new BillPaymentView(
            saga.Id, data.Status, saga.State, data.Biller, BillerCatalog.ByCode(data.Biller)?.Name ?? data.Biller, data.ConsumerNumber, data.CustomerName,
            data.AmountPaisa, data.BillingMonth, data.BillerReference, data.FailureCode, data.FailureReason, data.RequestedAt, saga.UpdatedAt, saga.CompletedAt,
            withTimeline ? Timeline(saga.Steps) : null);
    }
}
