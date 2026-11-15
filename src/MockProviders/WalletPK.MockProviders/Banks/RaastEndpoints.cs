using System.Text.Json;
using Npgsql;
using WalletPK.Common.Results;
using WalletPK.MockProviders.Callbacks;
using WalletPK.MockProviders.Common;
using WalletPK.MockProviders.Faults;
using WalletPK.MockProviders.Security;
using WalletPK.MockProviders.Storage;
using WalletPK.Web.Errors;

namespace WalletPK.MockProviders.Banks;

/// <param name="TransferId">The caller's unique ID; sending it again returns the same transfer.</param>
/// <param name="AmountPaisa">Rs. 1 to Rs. 1,000,000.</param>
/// <param name="ToIban">The beneficiary IBAN, or ...</param>
/// <param name="ToAlias">... their Raast ID (mobile number).</param>
/// <param name="SenderName">Shown to the beneficiary.</param>
/// <param name="Purpose">Free text, up to 140 characters.</param>
public sealed record RaastTransferRequest(string? TransferId, long AmountPaisa, string? ToIban, string? ToAlias, string? SenderName, string? Purpose);

/// <summary>A Raast transfer as the network reports it.</summary>
public sealed record RaastTransfer(
    string TransferId,
    string RaastReference,
    string Status,
    string? Reason,
    long AmountPaisa,
    string ToIban,
    string ToBank,
    string BeneficiaryTitle,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? CompletedAt);

/// <summary>What a confirmation callback carries (POSTed to Raast:CallbackUrl, signed).</summary>
public sealed record RaastCallback(string Event, string TransferId, string RaastReference, string Status, string? Reason, long AmountPaisa, string ToIban, string ToBank, DateTimeOffset CompletedAt);

/// <summary>
/// Mock Raast, Pakistan's instant payment system, with four member banks (M5.2):
/// <list type="bullet">
/// <item>GET  /raast/v1/aliases/{mobile}       Raast ID lookup: which account a mobile number points to</item>
/// <item>GET  /raast/v1/accounts/{iban}        title fetch: account holder and status</item>
/// <item>POST /raast/v1/transfers              send money: 202 Pending; the beneficiary bank settles it moments later and a signed callback follows</item>
/// <item>GET  /raast/v1/transfers/{transferId} status query (what to ask after a timeout)</item>
/// </list>
/// Faults: "raast" for everything, plus "bank:{code}" of the beneficiary bank. While a bank is in an
/// outage, new transfers to it get 503 and accepted ones stay Pending until it is back.
/// </summary>
public static class RaastEndpoints
{
    public const string TransferKind = "raast_transfer";
    private const long MaxAmountPaisa = 100_000_000;

    public static IEndpointRouteBuilder MapRaastEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/raast/v1").WithTags("Raast").RequireClientKey();

        group.MapGet("/aliases/{mobile}", (string mobile, HttpContext context, FaultInjector faults) =>
            faults.RunAsync(context, [Providers.Raast], () =>
            {
                if (Mobile.ToE164(mobile) is not { } e164)
                {
                    return Task.FromResult(MockErrors.Invalid("raast.invalid_alias", "A Raast ID is a Pakistani mobile number.").ToProblem());
                }

                return Task.FromResult(BankDirectory.ByAlias(e164) is { } account
                    ? Results.Ok(new { alias = e164, iban = account.Iban, bank = account.Bank.Code, bankName = account.Bank.Name, title = account.Title })
                    : Error.NotFound("raast.alias_not_found", "This mobile number has no Raast ID.").ToProblem());
            }));

        group.MapGet("/accounts/{iban}", (string iban, HttpContext context, FaultInjector faults) =>
        {
            if (Iban.Normalize(iban) is not { } normalized)
            {
                return Task.FromResult(InvalidIban());
            }

            if (BankDirectory.Account(normalized) is not { } account)
            {
                return Task.FromResult(UnknownBank());
            }

            return faults.RunAsync(context, [Providers.Raast, Providers.Bank(account.Bank.Code)], () =>
                Task.FromResult(Results.Ok(new { iban = account.Iban, bank = account.Bank.Code, bankName = account.Bank.Name, title = account.Title, status = account.Status })));
        });

        group.MapPost("/transfers", (RaastTransferRequest request, HttpContext context, FaultInjector faults, OperationStore store, MockProvidersOptions options, TimeProvider clock) =>
        {
            if (request.TransferId is not { Length: > 0 and <= 64 } transferId)
            {
                return Task.FromResult(MockErrors.Invalid("raast.invalid_transfer_id", "transferId is required (at most 64 characters).").ToProblem());
            }

            if (request.AmountPaisa is < 100 or > MaxAmountPaisa)
            {
                return Task.FromResult(MockErrors.Invalid("raast.amount_out_of_range", "amountPaisa must be between 100 (Rs. 1) and 100,000,000 (Rs. 1,000,000).").ToProblem());
            }

            if (request.Purpose is { Length: > 140 })
            {
                return Task.FromResult(MockErrors.Invalid("raast.invalid_purpose", "purpose is at most 140 characters.").ToProblem());
            }

            BankAccount? beneficiary;
            if (request.ToAlias is { Length: > 0 })
            {
                beneficiary = Mobile.ToE164(request.ToAlias) is { } e164 ? BankDirectory.ByAlias(e164) : null;
                if (beneficiary is null)
                {
                    return Task.FromResult(Error.NotFound("raast.alias_not_found", "This mobile number has no Raast ID.").ToProblem());
                }
            }
            else if (Iban.Normalize(request.ToIban) is { } iban)
            {
                beneficiary = BankDirectory.Account(iban);
                if (beneficiary is null)
                {
                    return Task.FromResult(UnknownBank());
                }
            }
            else
            {
                return Task.FromResult(InvalidIban());
            }

            return faults.RunAsync(context, [Providers.Raast, Providers.Bank(beneficiary.Bank.Code)], async () =>
            {
                var now = clock.GetUtcNow();
                var settleIn = Deterministic.Between(options.Raast.SettleDelayMinMilliseconds, options.Raast.SettleDelayMaxMilliseconds, "settle", transferId);
                var (stored, created) = await store.AddOrGetAsync(
                    new Operation
                    {
                        Provider = Providers.Raast,
                        ClientId = transferId,
                        Kind = TransferKind,
                        Reference = $"RAAST{now:yyyyMMdd}{Guid.NewGuid():N}"[..25].ToUpperInvariant(),
                        RequestHash = Operation.HashOf(request),
                        AmountPaisa = request.AmountPaisa,
                        Counterparty = beneficiary.Iban,
                        SettlementParty = Providers.Bank(beneficiary.Bank.Code),
                        Status = OperationStatus.Pending,
                        Details = JsonSerializer.Serialize(new RaastDetails(beneficiary.Bank.Code, beneficiary.Title, request.SenderName, request.Purpose), JsonSerializerOptions.Web),
                        CreatedAt = now.UtcDateTime,
                        ProcessAfter = now.AddMilliseconds(settleIn).UtcDateTime,
                    },
                    context.RequestAborted);

                if (!created && stored.RequestHash != Operation.HashOf(request))
                {
                    return MockErrors.DuplicateId("transferId").ToProblem();
                }

                return Results.Json(View(stored), statusCode: StatusCodes.Status202Accepted);
            });
        });

        group.MapGet("/transfers/{transferId}", (string transferId, HttpContext context, FaultInjector faults, OperationStore store) =>
            faults.RunAsync(context, [Providers.Raast], async () =>
                await store.GetAsync(Providers.Raast, transferId, context.RequestAborted) is { } transfer
                    ? Results.Ok(View(transfer))
                    : Error.NotFound("raast.transfer_not_found", "Raast never received a transfer with this ID.").ToProblem()));

        return endpoints;
    }

    internal static RaastTransfer View(Operation operation)
    {
        var details = operation.DetailsAs<RaastDetails>();
        return new RaastTransfer(
            operation.ClientId,
            operation.Reference,
            operation.Status,
            operation.Reason,
            operation.AmountPaisa,
            operation.Counterparty,
            details.Bank,
            details.Title,
            new DateTimeOffset(DateTime.SpecifyKind(operation.CreatedAt, DateTimeKind.Utc)),
            operation.CompletedAt is { } completed ? new DateTimeOffset(DateTime.SpecifyKind(completed, DateTimeKind.Utc)) : null);
    }

    private static IResult InvalidIban() =>
        MockErrors.Invalid("raast.invalid_iban", "Not a valid Pakistani IBAN (PK + 2 check digits + 4-letter bank code + 16 digits).").ToProblem();

    private static IResult UnknownBank() =>
        Error.NotFound("raast.unknown_bank", $"This bank is not on the network. Banks: {string.Join(", ", BankDirectory.All.Select(bank => $"{bank.Code} ({bank.IbanCode})"))}.").ToProblem();
}

internal sealed record RaastDetails(string Bank, string Title, string? SenderName, string? Purpose);

/// <summary>
/// The beneficiary banks' side: settles accepted transfers when they are due (closed or blocked accounts
/// are rejected) and queues the signed confirmation callback in the same transaction. A bank in an
/// outage settles nothing; its transfers wait and go through when it is back.
/// </summary>
internal sealed partial class RaastSettler(
    OperationStore store,
    FaultRegistry faults,
    MockProvidersOptions options,
    TimeProvider clock,
    ILogger<RaastSettler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await store.SettleDueAsync(RaastEndpoints.TransferKind, clock.GetUtcNow(), 50, SettleAsync, stoppingToken) > 0)
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            await Task.Delay(100, stoppingToken);
        }
    }

    private async Task<SettleOutcome?> SettleAsync(Operation transfer, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var details = transfer.DetailsAs<RaastDetails>();
        if (faults.For(Providers.Bank(details.Bank)) is { Outage: true })
        {
            return null; // Try again later.
        }

        var status = BankDirectory.Account(transfer.Counterparty)?.Status;
        var settlement = status switch
        {
            AccountStatus.Active => new SettleOutcome(OperationStatus.Completed, null),
            AccountStatus.Closed => new SettleOutcome(OperationStatus.Rejected, "account_closed"),
            _ => new SettleOutcome(OperationStatus.Rejected, "account_blocked"),
        };

        if (options.Raast.CallbackUrl.Length > 0)
        {
            var now = clock.GetUtcNow();
            var payload = JsonSerializer.Serialize(
                new RaastCallback(
                    settlement.Status == OperationStatus.Completed ? "raast.transfer.completed" : "raast.transfer.rejected",
                    transfer.ClientId,
                    transfer.Reference,
                    settlement.Status,
                    settlement.Reason,
                    transfer.AmountPaisa,
                    transfer.Counterparty,
                    details.Bank,
                    now),
                JsonSerializerOptions.Web);
            await CallbackOutbox.EnqueueAsync(connection, transaction, Providers.Raast, options.Raast.CallbackUrl, payload, now);
        }

        return settlement;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Raast settlement loop failed; retrying")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
