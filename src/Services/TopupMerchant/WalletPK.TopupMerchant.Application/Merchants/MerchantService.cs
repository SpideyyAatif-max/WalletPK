using System.Text;
using WalletPK.Common.Results;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging.Events;
using WalletPK.Sagas;
using WalletPK.TopupMerchant.Application.TopUps;
using WalletPK.TopupMerchant.Domain;

namespace WalletPK.TopupMerchant.Application.Merchants;

/// <summary>A business that takes QR payments (M8.2). Active once the Ledger has opened its wallet.</summary>
public sealed class Merchant
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    /// <summary>"WPKM123456": the merchant wallet's number.</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public string Category { get; set; } = "";

    public string FeePlan { get; set; } = "";

    public string? City { get; set; }

    /// <summary>Pending (wallet being opened) or Active.</summary>
    public string Status { get; set; } = "Pending";

    public Guid? WalletId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A dynamic QR code (M8.3): one amount, one payment, a few minutes.</summary>
public sealed class QrCode
{
    public Guid Id { get; set; }

    public Guid MerchantId { get; set; }

    public long AmountPaisa { get; set; }

    public string? Reference { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The payment that claimed it (null: open). A failed payment frees it again.</summary>
    public Guid? PaymentId { get; set; }
}

/// <summary>One night's sweep of a merchant's sales to its bank (M8.5).</summary>
public sealed class MerchantSettlement
{
    public Guid Id { get; set; }

    public Guid MerchantId { get; set; }

    public DateOnly BusinessDate { get; set; }

    public int PaymentCount { get; set; }

    public long GrossPaisa { get; set; }

    public long FeePaisa { get; set; }

    public long TaxPaisa { get; set; }

    public long NetPaisa { get; set; }

    /// <summary>Pending (sent to the Ledger), Settled or Failed.</summary>
    public string Status { get; set; } = "Pending";

    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When SettleMerchant was last sent (it is sent again while unanswered).</summary>
    public DateTimeOffset SentAt { get; set; }

    public DateTimeOffset? SettledAt { get; set; }
}

/// <summary>A payment included in a settlement (so it is never settled twice).</summary>
public sealed class SettledPayment
{
    public Guid PaymentId { get; set; }

    public Guid SettlementId { get; set; }
}

/// <summary>Port: merchants, QR codes and settlements.</summary>
public interface IMerchantStore
{
    Task<Merchant?> ByOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken);

    Task<Merchant?> ByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Merchant?> ByCodeAsync(string code, CancellationToken cancellationToken);

    void Add(Merchant merchant);

    void Add(QrCode code);

    Task<QrCode?> QrAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Claims an open, unexpired QR code for <paramref name="paymentId"/> in one statement: open means no
    /// payment yet, or only a failed one. False when someone else got it first, or it expired.
    /// </summary>
    Task<bool> TryClaimAsync(Guid qrId, Guid paymentId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<MerchantSettlement>> SettlementsAsync(Guid merchantId, int limit, CancellationToken cancellationToken);

    /// <summary>Completed payments to the merchant not yet in a settlement: count, gross, fees, tax, net.</summary>
    Task<UnsettledTotals> UnsettledAsync(Guid merchantId, CancellationToken cancellationToken);

    /// <summary>Queues a message in the outbox: it is sent if, and only if, the next save commits.</summary>
    void Publish(IntegrationEvent message, string? correlationId);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Completed payments not yet settled.</summary>
public sealed record UnsettledTotals(int Count, long GrossPaisa, long FeePaisa, long TaxPaisa, long NetPaisa);

/// <summary>QR settings, from "Merchants:Qr".</summary>
public sealed class QrOptions
{
    public const string SectionName = "Merchants:Qr";

    /// <summary>The HMAC key QR payloads are signed with (at least 32 characters).</summary>
    public string SigningKey { get; set; } = "";

    public byte[] Key => Encoding.UTF8.GetBytes(SigningKey);

    public bool IsValid() => SigningKey.Length >= 32;
}

public sealed record MerchantView(
    Guid Id,
    string Code,
    string Name,
    string Category,
    string? City,
    string Status,
    FeePlan FeePlan,
    int UnsettledPayments,
    long UnsettledGrossPaisa,
    long UnsettledNetPaisa,
    DateTimeOffset CreatedAt);

public sealed record QrView(Guid Id, string Payload, long AmountPaisa, string? Reference, DateTimeOffset ExpiresAt, string Status, Guid? PaymentId, DateTimeOffset? PaidAt, long? NetPaisa);

/// <summary>What a payer sees after scanning, before paying.</summary>
public sealed record QrPreview(Guid QrId, string MerchantCode, string MerchantName, string Category, long AmountPaisa, string? Reference, DateTimeOffset ExpiresAt);

public sealed record MerchantPaymentView(
    Guid Id,
    string Status,
    string MerchantCode,
    string MerchantName,
    long AmountPaisa,
    long FeePaisa,
    long TaxPaisa,
    long NetPaisa,
    string? Reference,
    string? PayerWalletNumber,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset RequestedAt,
    DateTimeOffset? PaidAt);

public sealed record SettlementView(Guid Id, DateOnly BusinessDate, int PaymentCount, long GrossPaisa, long FeePaisa, long TaxPaisa, long NetPaisa, string Status, string? FailureReason, DateTimeOffset? SettledAt);

public static class MerchantErrors
{
    public static readonly Error AlreadyMerchant = Error.Conflict("merchant.already_registered", "You already have a merchant account.");
    public static readonly Error NotMerchant = Error.NotFound("merchant.not_found", "You do not have a merchant account.");
    public static readonly Error NotActive = Error.Conflict("merchant.not_active", "Your merchant account is still being set up; try again in a moment.");
    public static readonly Error InvalidName = Error.Validation("merchant.invalid_name", $"The business name is 2 to {MerchantRules.MaxNameLength} characters.");
    public static readonly Error InvalidCategory = Error.Validation("merchant.invalid_category", $"Categories: {string.Join(", ", FeePlans.Categories.Select(category => category.Code))}.");
    public static readonly Error InvalidAmount = Error.Validation("merchant.invalid_amount", "A QR payment is between Rs. 10 and Rs. 500,000.");
    public static readonly Error InvalidExpiry = Error.Validation("merchant.invalid_expiry", $"A QR code lasts {MerchantRules.MinQrSeconds} to {MerchantRules.MaxQrSeconds} seconds.");
    public static readonly Error InvalidReference = Error.Validation("merchant.invalid_reference", $"A reference is at most {MerchantRules.MaxReferenceLength} characters.");
    public static readonly Error InvalidQr = Error.Validation("merchant.invalid_qr", "This is not a valid WalletPK QR code.");
    public static readonly Error QrExpired = Error.Conflict("merchant.qr_expired", "This QR code has expired; ask the merchant for a new one.");
    public static readonly Error QrPaid = Error.Conflict("merchant.qr_paid", "This QR code has already been paid.");
    public static readonly Error OwnQr = Error.Validation("merchant.own_qr", "You cannot pay your own QR code.");
    public static readonly Error QrNotFound = Error.NotFound("merchant.qr_not_found", "No such QR code.");
    public static readonly Error PaymentNotFound = Error.NotFound("merchant.payment_not_found", "No such payment.");
}

/// <summary>Merchants (M8.2), their QR codes (M8.3), QR payments (M8.4) and their settlements (M8.5).</summary>
public sealed class MerchantService(
    IMerchantStore store,
    ISagaEngine engine,
    ISagaReader reader,
    ISagaLists lists,
    MerchantPaymentSaga payments,
    QrOptions qr,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Registers the caller's business; its merchant wallet is opened by the Ledger (OpenMerchantWallet,
    /// sent through the outbox with the new row), and it becomes Active when the Ledger answers.
    /// </summary>
    public async Task<Result<MerchantView>> RegisterAsync(Guid ownerUserId, string? name, string? category, string? city, string? correlationId, CancellationToken cancellationToken)
    {
        if (await store.ByOwnerAsync(ownerUserId, cancellationToken) is not null)
        {
            return MerchantErrors.AlreadyMerchant;
        }

        var cleanName = name?.Trim();
        if (cleanName is not { Length: >= 2 and <= MerchantRules.MaxNameLength })
        {
            return MerchantErrors.InvalidName;
        }

        if (FeePlans.Category(category) is not { } kind)
        {
            return MerchantErrors.InvalidCategory;
        }

        // Six digits: pick another ID in the rare case its code is taken.
        var id = Guid.CreateVersion7();
        while (await store.ByCodeAsync(CodeFor(id), cancellationToken) is not null)
        {
            id = Guid.CreateVersion7();
        }

        var merchant = new Merchant
        {
            Id = id,
            OwnerUserId = ownerUserId,
            Code = CodeFor(id),
            Name = cleanName,
            Category = kind.Code,
            FeePlan = kind.FeePlan,
            City = string.IsNullOrWhiteSpace(city) ? null : city.Trim()[..Math.Min(city.Trim().Length, 40)],
            CreatedAt = timeProvider.GetUtcNow(),
        };
        store.Add(merchant);
        store.Publish(new OpenMerchantWallet(merchant.Id, ownerUserId, merchant.Code, merchant.Name), correlationId);
        await store.SaveChangesAsync(cancellationToken);
        return await ViewAsync(merchant, cancellationToken);
    }

    public async Task<Result<MerchantView>> MeAsync(Guid ownerUserId, CancellationToken cancellationToken) =>
        await store.ByOwnerAsync(ownerUserId, cancellationToken) is { } merchant ? await ViewAsync(merchant, cancellationToken) : MerchantErrors.NotMerchant;

    /// <summary>A signed QR code for exactly <paramref name="amountPaisa"/>, payable once until it expires.</summary>
    public async Task<Result<QrView>> CreateQrAsync(Guid ownerUserId, long amountPaisa, string? reference, int? expiresInSeconds, CancellationToken cancellationToken)
    {
        if (await store.ByOwnerAsync(ownerUserId, cancellationToken) is not { } merchant)
        {
            return MerchantErrors.NotMerchant;
        }

        if (merchant.Status != "Active")
        {
            return MerchantErrors.NotActive;
        }

        if (amountPaisa is < MerchantRules.MinAmountPaisa or > MerchantRules.MaxAmountPaisa)
        {
            return MerchantErrors.InvalidAmount;
        }

        if (expiresInSeconds is < MerchantRules.MinQrSeconds or > MerchantRules.MaxQrSeconds)
        {
            return MerchantErrors.InvalidExpiry;
        }

        var cleanReference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        if (cleanReference is { Length: > MerchantRules.MaxReferenceLength })
        {
            return MerchantErrors.InvalidReference;
        }

        var now = timeProvider.GetUtcNow();
        var code = new QrCode
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchant.Id,
            AmountPaisa = amountPaisa,
            Reference = cleanReference,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(expiresInSeconds ?? MerchantRules.DefaultQrSeconds),
        };
        store.Add(code);
        await store.SaveChangesAsync(cancellationToken);
        return await QrViewAsync(merchant, code, cancellationToken);
    }

    /// <summary>A QR code's state, for the merchant's till (Open, Paid, Expired).</summary>
    public async Task<Result<QrView>> QrAsync(Guid ownerUserId, Guid qrId, CancellationToken cancellationToken)
    {
        if (await store.ByOwnerAsync(ownerUserId, cancellationToken) is not { } merchant)
        {
            return MerchantErrors.NotMerchant;
        }

        return await store.QrAsync(qrId, cancellationToken) is { } code && code.MerchantId == merchant.Id
            ? await QrViewAsync(merchant, code, cancellationToken)
            : MerchantErrors.QrNotFound;
    }

    /// <summary>What a scanned payload asks for, checked (signature, expiry, not paid) before the payer confirms.</summary>
    public async Task<Result<QrPreview>> PreviewAsync(Guid payerUserId, string? payload, CancellationToken cancellationToken)
    {
        var checkedQr = await CheckAsync(payerUserId, payload, cancellationToken);
        if (checkedQr.IsFailure)
        {
            return checkedQr.Error;
        }

        var (merchant, code) = checkedQr.Value;
        return new QrPreview(code.Id, merchant.Code, merchant.Name, merchant.Category, code.AmountPaisa, code.Reference, code.ExpiresAt);
    }

    /// <summary>
    /// Pays a QR code (M8.4): claims it (so it is paid once even if two people scan it), then starts the
    /// payment Saga with the merchant's fee split worked out from its fee plan.
    /// </summary>
    public async Task<Result<MerchantPaymentView>> PayAsync(
        Guid payerUserId,
        string? payerWalletNumber,
        string? deviceId,
        string idempotencyKey,
        string? payload,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var paymentId = TopUpService.SagaId("merchant-payment", payerUserId, idempotencyKey);

        // A retry of a payment already started returns it.
        if (await reader.FindAsync<MerchantPaymentData>(paymentId, cancellationToken) is { } existing)
        {
            return existing.Data.PayerUserId == payerUserId ? ToView(existing) : MerchantErrors.PaymentNotFound;
        }

        var checkedQr = await CheckAsync(payerUserId, payload, cancellationToken);
        if (checkedQr.IsFailure)
        {
            return checkedQr.Error;
        }

        var (merchant, code) = checkedQr.Value;
        var split = (FeePlans.ByCode(merchant.FeePlan) ?? FeePlans.All[0]).Split(code.AmountPaisa);
        var data = new MerchantPaymentData
        {
            PayerUserId = payerUserId,
            PayerWalletNumber = Mask(payerWalletNumber),
            DeviceId = deviceId,
            IdempotencyKey = idempotencyKey,
            MerchantId = merchant.Id,
            MerchantOwnerUserId = merchant.OwnerUserId,
            MerchantCode = merchant.Code,
            MerchantName = merchant.Name,
            QrId = code.Id,
            Reference = code.Reference,
            AmountPaisa = split.AmountPaisa,
            FeePaisa = split.FeePaisa,
            TaxPaisa = split.TaxPaisa,
            NetPaisa = split.NetPaisa,
            RequestedAt = timeProvider.GetUtcNow(),
        };

        var outcome = await engine.InTransactionAsync(
            async () =>
            {
                if (!await store.TryClaimAsync(code.Id, paymentId, timeProvider.GetUtcNow(), cancellationToken))
                {
                    return false;
                }

                await engine.StartAsync(payments, paymentId, data, correlationId, cancellationToken);
                return true;
            },
            cancellationToken);

        if (!outcome)
        {
            return code.ExpiresAt <= timeProvider.GetUtcNow() ? MerchantErrors.QrExpired : MerchantErrors.QrPaid;
        }

        return ToView((await reader.FindAsync<MerchantPaymentData>(paymentId, cancellationToken))!);
    }

    public async Task<Result<MerchantPaymentView>> PaymentAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken)
    {
        var saga = await reader.FindAsync<MerchantPaymentData>(paymentId, cancellationToken);
        if (saga is null || saga.SagaType != MerchantPaymentSaga.SagaName)
        {
            return MerchantErrors.PaymentNotFound;
        }

        return saga.Data.PayerUserId == userId || saga.Data.MerchantOwnerUserId == userId ? ToView(saga) : MerchantErrors.PaymentNotFound;
    }

    /// <summary>The merchant's payments, newest first.</summary>
    public async Task<Result<IReadOnlyList<MerchantPaymentView>>> PaymentsAsync(Guid ownerUserId, int? limit, CancellationToken cancellationToken)
    {
        if (await store.ByOwnerAsync(ownerUserId, cancellationToken) is not { } merchant)
        {
            return MerchantErrors.NotMerchant;
        }

        var sagas = await lists.ByFieldAsync<MerchantPaymentData>(MerchantPaymentSaga.SagaName, "merchantId", merchant.Id.ToString(), Math.Clamp(limit ?? 20, 1, 100), cancellationToken);
        return Result.Success<IReadOnlyList<MerchantPaymentView>>([.. sagas.Select(ToView)]);
    }

    /// <summary>The payer's QR payments, newest first.</summary>
    public async Task<IReadOnlyList<MerchantPaymentView>> PaidByAsync(Guid payerUserId, int? limit, CancellationToken cancellationToken) =>
        [.. (await lists.ByFieldAsync<MerchantPaymentData>(MerchantPaymentSaga.SagaName, "payerUserId", payerUserId.ToString(), Math.Clamp(limit ?? 20, 1, 100), cancellationToken)).Select(ToView)];

    public async Task<Result<IReadOnlyList<SettlementView>>> SettlementsAsync(Guid ownerUserId, int? limit, CancellationToken cancellationToken)
    {
        if (await store.ByOwnerAsync(ownerUserId, cancellationToken) is not { } merchant)
        {
            return MerchantErrors.NotMerchant;
        }

        var settlements = await store.SettlementsAsync(merchant.Id, Math.Clamp(limit ?? 30, 1, 100), cancellationToken);
        return Result.Success<IReadOnlyList<SettlementView>>([.. settlements.Select(settlement => new SettlementView(
            settlement.Id, settlement.BusinessDate, settlement.PaymentCount, settlement.GrossPaisa, settlement.FeePaisa, settlement.TaxPaisa,
            settlement.NetPaisa, settlement.Status, settlement.FailureReason, settlement.SettledAt))]);
    }

    // ---- Helpers ----

    private async Task<Result<(Merchant Merchant, QrCode Code)>> CheckAsync(Guid payerUserId, string? payload, CancellationToken cancellationToken)
    {
        if (QrCodec.Decode(payload, qr.Key) is not { } content)
        {
            return MerchantErrors.InvalidQr;
        }

        if (await store.QrAsync(content.QrId, cancellationToken) is not { } code
            || await store.ByIdAsync(code.MerchantId, cancellationToken) is not { } merchant
            || merchant.Code != content.MerchantCode
            || code.AmountPaisa != content.AmountPaisa)
        {
            return MerchantErrors.InvalidQr;
        }

        if (merchant.OwnerUserId == payerUserId)
        {
            return MerchantErrors.OwnQr;
        }

        if (code.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return MerchantErrors.QrExpired;
        }

        if (code.PaymentId is { } claimed && await reader.FindAsync<MerchantPaymentData>(claimed, cancellationToken) is { State: not "Failed" })
        {
            return MerchantErrors.QrPaid;
        }

        return (merchant, code);
    }

    private async Task<MerchantView> ViewAsync(Merchant merchant, CancellationToken cancellationToken)
    {
        var unsettled = await store.UnsettledAsync(merchant.Id, cancellationToken);
        return new MerchantView(
            merchant.Id, merchant.Code, merchant.Name, merchant.Category, merchant.City, merchant.Status, FeePlans.ByCode(merchant.FeePlan) ?? FeePlans.All[0],
            unsettled.Count, unsettled.GrossPaisa, unsettled.NetPaisa, merchant.CreatedAt);
    }

    private async Task<QrView> QrViewAsync(Merchant merchant, QrCode code, CancellationToken cancellationToken)
    {
        var payment = code.PaymentId is { } id ? await reader.FindAsync<MerchantPaymentData>(id, cancellationToken) : null;
        var status = payment?.State switch
        {
            "Completed" => "Paid",
            "Paying" or "NeedsReview" => "Paying",
            _ => code.ExpiresAt <= timeProvider.GetUtcNow() ? "Expired" : "Open",
        };
        var payload = QrCodec.Encode(new QrContent(code.Id, merchant.Code, merchant.Name, code.AmountPaisa, code.Reference, code.ExpiresAt), qr.Key);
        return new QrView(code.Id, payload, code.AmountPaisa, code.Reference, code.ExpiresAt, status, payment?.Id, payment?.Data.PaidAt, payment?.Data.NetPaisa);
    }

    /// <summary>The merchant sees "+92300•••4567", not the payer's whole number.</summary>
    internal static string? Mask(string? number) =>
        number is { Length: >= 10 } ? $"{number[..^7]}•••{number[^4..]}" : null;

    /// <summary>"WPKM" and six digits derived from the merchant's ID.</summary>
    internal static string CodeFor(Guid merchantId)
    {
        var number = Math.Abs(BitConverter.ToInt64(merchantId.ToByteArray(), 8) % 1_000_000);
        return $"WPKM{number:000000}";
    }

    internal static MerchantPaymentView ToView(SagaSnapshot<MerchantPaymentData> saga)
    {
        var data = saga.Data;
        return new MerchantPaymentView(
            saga.Id, data.Status, data.MerchantCode, data.MerchantName, data.AmountPaisa, data.FeePaisa, data.TaxPaisa, data.NetPaisa, data.Reference,
            data.PayerWalletNumber, data.FailureCode, data.FailureReason, data.RequestedAt, data.PaidAt);
    }
}
