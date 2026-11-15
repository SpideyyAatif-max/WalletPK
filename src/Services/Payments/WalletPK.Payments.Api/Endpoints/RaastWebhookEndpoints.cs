using System.Globalization;
using System.Text.Json;
using WalletPK.Common.Results;
using WalletPK.Payments.Application.BankTransfers;
using WalletPK.Payments.Infrastructure;
using WalletPK.Sagas;
using WalletPK.Web.Errors;
using WalletPK.Web.Security;

namespace WalletPK.Payments.Api.Endpoints;

/// <summary>What Raast POSTs when a transfer settles.</summary>
public sealed record RaastCallbackPayload(string? Event, string? TransferId, string? RaastReference, string? Status, string? Reason, long AmountPaisa, string? ToIban, string? ToBank, DateTimeOffset? CompletedAt);

/// <summary>
/// POST /webhooks/raast/transfer-status: Raast's confirmation that a transfer completed or was rejected.
/// Arrives through the gateway, which has already checked the signature; it is checked again here, so a
/// request that reaches the service some other way (inside the network) cannot fake a payment.
/// </summary>
public static partial class RaastWebhookEndpoints
{
    private const int MaxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapRaastWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/webhooks/raast/transfer-status", HandleAsync).WithTags("Webhooks").AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        RaastWebhookOptions options,
        BankTransferService transfers,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(RaastWebhookEndpoints));
        if (request.ContentLength > MaxBodyBytes)
        {
            return Reject(logger, "webhook.too_large", "The webhook body is too large.");
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var body = buffer.ToArray();

        if (options.Secret.Length == 0)
        {
            return Reject(logger, "webhook.not_configured", "Raast callbacks are not configured.");
        }

        if (body.Length > MaxBodyBytes
            || !long.TryParse(request.Headers[WebhookSignature.TimestampHeader], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || Math.Abs(timeProvider.GetUtcNow().ToUnixTimeSeconds() - timestamp) > options.ToleranceSeconds
            || !WebhookSignature.Verify(options.Secret, timestamp, body, request.Headers[WebhookSignature.SignatureHeader]))
        {
            return Reject(logger, "webhook.invalid_signature", "The webhook signature or timestamp is invalid.");
        }

        RaastCallbackPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<RaastCallbackPayload>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is not { TransferId: { } transferIdText, RaastReference: { } reference, Status: { } status })
        {
            return Error.Validation("webhook.invalid_payload", "transferId, raastReference and status are required.").ToProblem();
        }

        if (!Guid.TryParse(transferIdText, out var transferId))
        {
            return BankTransferErrors.NotFound.ToProblem();
        }

        var outcome = await transfers.ReceiveCallbackAsync(new RaastTransferCallback(transferId, reference, status, payload.Reason), cancellationToken);
        LogReceived(logger, transferId, status, reference, outcome);
        return outcome == SagaHandleOutcome.NotFound
            ? BankTransferErrors.NotFound.ToProblem()
            : TypedResults.Ok(new { received = true });
    }

    private static IResult Reject(ILogger logger, string code, string detail)
    {
        LogRejected(logger, code);
        return Error.Unauthorized(code, detail).ToProblem();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Raast callback for {TransferId}: {Status} ({Reference}) → {Outcome}")]
    private static partial void LogReceived(ILogger logger, Guid transferId, string status, string reference, SagaHandleOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a Raast callback: {Code}")]
    private static partial void LogRejected(ILogger logger, string code);
}
