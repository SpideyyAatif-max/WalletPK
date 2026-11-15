using System.Security.Cryptography;
using System.Text;
using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Payments.Application.Raast;
using WalletPK.Payments.Domain;
using WalletPK.Sagas;

namespace WalletPK.Payments.Application.BankTransfers;

/// <param name="UserId">Who sends.</param>
/// <param name="DeviceId">From which device.</param>
/// <param name="IdempotencyKey">The client's key: the same key again returns the same transfer.</param>
/// <param name="ToIban">The beneficiary's IBAN.</param>
/// <param name="AmountPaisa">How much.</param>
/// <param name="BeneficiaryTitle">The name a lookup showed the customer (optional).</param>
/// <param name="Note">A note to the beneficiary (optional).</param>
/// <param name="CorrelationId">The request's correlation ID (every message of the transfer carries it).</param>
public sealed record SendBankTransferCommand(
    Guid UserId,
    string? DeviceId,
    string IdempotencyKey,
    string? ToIban,
    long AmountPaisa,
    string? BeneficiaryTitle,
    string? Note,
    string? CorrelationId);

/// <summary>One entry of the transfer tracker.</summary>
public sealed record BankTransferStep(int Sequence, string State, string Detail, DateTimeOffset At);

public sealed record BankTransferView(
    Guid Id,
    string Status,
    string Step,
    long AmountPaisa,
    string ToIban,
    string ToBank,
    string ToBankName,
    string? BeneficiaryTitle,
    string? Note,
    string? RaastReference,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<BankTransferStep>? Timeline);

/// <param name="Transfer">The transfer.</param>
/// <param name="Created">False when the Idempotency-Key had already started it.</param>
public sealed record SentBankTransfer(BankTransferView Transfer, bool Created);

/// <summary>The beneficiary a lookup found.</summary>
public sealed record Beneficiary(string Iban, string MaskedIban, string Bank, string BankName, string Title, string AccountStatus, bool CanReceive);

/// <summary>Port for listing a customer's transfers (a query over saga_instances).</summary>
public interface IBankTransferQueries
{
    Task<IReadOnlyList<SagaSnapshot<BankTransferData>>> ListAsync(Guid userId, int limit, DateTimeOffset? before, CancellationToken cancellationToken);
}

public static class BankTransferErrors
{
    public static readonly Error InvalidIban = Error.Validation("payments.invalid_iban", "Not a valid Pakistani IBAN (PK + 2 check digits + 4-letter bank code + 16 digits).");
    public static readonly Error BankNotOnRaast = Error.Validation("payments.bank_not_supported", $"This bank is not on Raast. Banks: {string.Join(", ", RaastBanks.All.Select(bank => bank.Name))}.");
    public static readonly Error InvalidAlias = Error.Validation("payments.invalid_raast_id", "A Raast ID is a Pakistani mobile number.");
    public static readonly Error LookupNeedsOne = Error.Validation("payments.lookup_target", "Give either an IBAN or a Raast ID (mobile number).");
    public static readonly Error AmountOutOfRange = Error.Validation(
        "payments.amount_out_of_range",
        $"A bank transfer is between {BankTransferSaga.Rupees(BankTransferRules.MinAmountPaisa)} and {BankTransferSaga.Rupees(BankTransferRules.MaxAmountPaisa)}.");
    public static readonly Error NoteTooLong = Error.Validation("payments.note_too_long", $"The note is at most {BankTransferRules.MaxNoteLength} characters.");
    public static readonly Error TitleTooLong = Error.Validation("payments.title_too_long", $"The beneficiary name is at most {BankTransferRules.MaxTitleLength} characters.");
    public static readonly Error KeyReused = Error.Conflict("payments.idempotency_key_reused", "This Idempotency-Key was already used for a different transfer.");
    public static readonly Error NotFound = Error.NotFound("payments.transfer_not_found", "No such bank transfer.");
    public static readonly Error AccountNotFound = Error.NotFound("payments.beneficiary_not_found", "No account was found for these details.");

    public static Error Unavailable(string? detail) => Error.Unavailable("payments.lookup_unavailable", $"The bank could not be reached ({detail}). Try again in a moment.");
}

/// <summary>Bank transfers over Raast: look up, send, track.</summary>
public sealed class BankTransferService(
    ISagaEngine engine,
    ISagaReader reader,
    BankTransferSaga definition,
    IBankTransferQueries queries,
    IRaastGateway raast,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Accepts a transfer: validates it and starts its Saga. Nothing outside is called here, so a
    /// transfer is accepted even while its bank is down (it waits in the queue).
    /// </summary>
    public async Task<Result<SentBankTransfer>> SendAsync(SendBankTransferCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (Iban.Normalize(command.ToIban) is not { } iban)
        {
            return BankTransferErrors.InvalidIban;
        }

        if (RaastBanks.ForIban(iban) is not { } bank)
        {
            return BankTransferErrors.BankNotOnRaast;
        }

        if (command.AmountPaisa is < BankTransferRules.MinAmountPaisa or > BankTransferRules.MaxAmountPaisa)
        {
            return BankTransferErrors.AmountOutOfRange;
        }

        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        if (note is { Length: > BankTransferRules.MaxNoteLength })
        {
            return BankTransferErrors.NoteTooLong;
        }

        var title = string.IsNullOrWhiteSpace(command.BeneficiaryTitle) ? null : command.BeneficiaryTitle.Trim();
        if (title is { Length: > BankTransferRules.MaxTitleLength })
        {
            return BankTransferErrors.TitleTooLong;
        }

        var id = TransferIdFor(command.UserId, command.IdempotencyKey);
        var data = new BankTransferData
        {
            UserId = command.UserId,
            IdempotencyKey = command.IdempotencyKey,
            AmountPaisa = command.AmountPaisa,
            ToIban = iban,
            ToBank = bank.Code,
            BeneficiaryTitle = title,
            Note = note,
            DeviceId = command.DeviceId,
            RequestedAt = timeProvider.GetUtcNow(),
        };

        var created = await engine.StartAsync(definition, id, data, command.CorrelationId, cancellationToken);
        var saga = await reader.FindAsync<BankTransferData>(id, cancellationToken)
            ?? throw new InvalidOperationException($"Bank transfer {id} vanished right after it was started.");

        // The same key with a different transfer is a client bug, not a retry.
        if (!created && (saga.Data.UserId != data.UserId || saga.Data.AmountPaisa != data.AmountPaisa || saga.Data.ToIban != data.ToIban))
        {
            return BankTransferErrors.KeyReused;
        }

        return new SentBankTransfer(ToView(saga, withTimeline: true), created);
    }

    /// <summary>A transfer with its timeline; only its sender can see it.</summary>
    public async Task<Result<BankTransferView>> GetAsync(Guid userId, Guid transferId, CancellationToken cancellationToken)
    {
        var saga = await reader.FindAsync<BankTransferData>(transferId, cancellationToken);
        return saga is null || saga.SagaType != BankTransferSaga.SagaName || saga.Data.UserId != userId
            ? BankTransferErrors.NotFound
            : ToView(saga, withTimeline: true);
    }

    /// <summary>The customer's transfers, newest first.</summary>
    public async Task<IReadOnlyList<BankTransferView>> ListAsync(Guid userId, int? limit, DateTimeOffset? before, CancellationToken cancellationToken)
    {
        var sagas = await queries.ListAsync(userId, Math.Clamp(limit ?? 20, 1, 100), before, cancellationToken);
        return [.. sagas.Select(saga => ToView(saga, withTimeline: false))];
    }

    /// <summary>Who is behind an IBAN or a Raast ID (shown to the customer before they send).</summary>
    public async Task<Result<Beneficiary>> LookupAsync(string? iban, string? raastId, CancellationToken cancellationToken)
    {
        RaastLookupResult result;
        if (!string.IsNullOrWhiteSpace(iban) && string.IsNullOrWhiteSpace(raastId))
        {
            if (Iban.Normalize(iban) is not { } normalized)
            {
                return BankTransferErrors.InvalidIban;
            }

            if (RaastBanks.ForIban(normalized) is not { } bank)
            {
                return BankTransferErrors.BankNotOnRaast;
            }

            result = await raast.FetchTitleAsync(normalized, bank, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(raastId) && string.IsNullOrWhiteSpace(iban))
        {
            if (!PakistaniMobileNumber.TryNormalize(raastId, out var mobile))
            {
                return BankTransferErrors.InvalidAlias;
            }

            result = await raast.LookupAliasAsync(mobile, cancellationToken);
        }
        else
        {
            return BankTransferErrors.LookupNeedsOne;
        }

        return result switch
        {
            { Outcome: RaastCallOutcome.Ok, Account: { } account } => new Beneficiary(
                account.Iban,
                Iban.Mask(account.Iban),
                account.BankCode,
                account.BankName,
                account.Title,
                account.Status,
                account.Status == "Active"),
            { Outcome: RaastCallOutcome.NotFound or RaastCallOutcome.Rejected } => BankTransferErrors.AccountNotFound,
            _ => BankTransferErrors.Unavailable(result.Detail),
        };
    }

    /// <summary>Raast's confirmation callback (already authenticated by its signature).</summary>
    public Task<SagaHandleOutcome> ReceiveCallbackAsync(RaastTransferCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return engine.HandleAsync(callback.TransferId, callback, $"raast:{callback.RaastReference}:{callback.Status}", cancellationToken);
    }

    /// <summary>The transfer ID for a customer's Idempotency-Key: the same key always gives the same transfer.</summary>
    public static Guid TransferIdFor(Guid userId, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"walletpk:payments:bank-transfer:{userId}:{idempotencyKey}"), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8 (custom)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }

    internal static BankTransferView ToView(SagaSnapshot<BankTransferData> saga, bool withTimeline)
    {
        var data = saga.Data;
        return new BankTransferView(
            saga.Id,
            data.Status,
            saga.State,
            data.AmountPaisa,
            Iban.Mask(data.ToIban),
            data.ToBank,
            RaastBanks.ByCode(data.ToBank)?.Name ?? data.ToBank,
            data.BeneficiaryTitle,
            data.Note,
            data.RaastReference,
            data.FailureCode,
            data.FailureReason,
            data.RequestedAt,
            saga.UpdatedAt,
            saga.CompletedAt,
            withTimeline ? [.. saga.Steps.Select(step => new BankTransferStep(step.Sequence, step.State, step.Detail, step.At))] : null);
    }
}
