using System.Text.Json;
using System.Text.RegularExpressions;
using WalletPK.MockProviders.Common;
using WalletPK.MockProviders.Faults;
using WalletPK.MockProviders.Security;
using WalletPK.MockProviders.Storage;
using WalletPK.Web.Errors;

namespace WalletPK.MockProviders.Messaging;

/// <param name="MessageId">The caller's unique ID; sending it again returns the first message and sends nothing.</param>
/// <param name="To">A Pakistani mobile number.</param>
/// <param name="Text">At most 1,000 characters (Urdu included).</param>
public sealed record SmsRequest(string? MessageId, string? To, string? Text);

/// <param name="MessageId">The caller's unique ID; sending it again returns the first message and sends nothing.</param>
/// <param name="To">An email address.</param>
/// <param name="Subject">At most 200 characters.</param>
/// <param name="Body">At most 10,000 characters.</param>
public sealed record EmailRequest(string? MessageId, string? To, string? Subject, string? Body);

/// <summary>A message the gateway accepted.</summary>
/// <param name="MessageId">The caller's ID.</param>
/// <param name="Reference">The gateway's own reference.</param>
/// <param name="Channel">"sms" or "email".</param>
/// <param name="To">Where it went.</param>
/// <param name="Subject">Emails only.</param>
/// <param name="Text">The SMS text or email body.</param>
/// <param name="AcceptedAt">When the gateway took it.</param>
public sealed record SentMessage(string MessageId, string Reference, string Channel, string To, string? Subject, string Text, DateTimeOffset AcceptedAt);

/// <summary>
/// Mock SMS and email gateways (M11.4), idempotent by the caller's message ID like a real gateway: a retry
/// after a timeout gets the first message back instead of texting the customer twice.
/// <list type="bullet">
/// <item>POST /sms/v1/messages            send a text</item>
/// <item>GET  /sms/v1/messages?to=        texts sent to a number, newest first (to read them in development)</item>
/// <item>POST /email/v1/messages          send an email</item>
/// <item>GET  /email/v1/messages?to=      emails sent to an address</item>
/// </list>
/// Faults: "sms", "email".
/// </summary>
public static partial class MessagingEndpoints
{
    public const string SmsKind = "sms";
    public const string EmailKind = "email";

    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var sms = endpoints.MapGroup("/sms/v1").WithTags("SMS gateway").RequireClientKey();
        sms.MapPost("/messages", (SmsRequest request, HttpContext context, FaultInjector faults, OperationStore store, TimeProvider clock) =>
        {
            var to = Mobile.ToE164(request.To);
            if (Invalid(request.MessageId) is { } badId)
            {
                return Task.FromResult(badId);
            }

            if (to is null)
            {
                return Task.FromResult(MockErrors.Invalid("sms.invalid_number", "to must be a Pakistani mobile number.").ToProblem());
            }

            if (request.Text is not { Length: > 0 and <= 1000 } text)
            {
                return Task.FromResult(MockErrors.Invalid("sms.invalid_text", "text is required (at most 1,000 characters).").ToProblem());
            }

            return faults.RunAsync(context, [Providers.Sms], () =>
                AcceptAsync(store, clock, Providers.Sms, SmsKind, request.MessageId!, to, null, text, request, context.RequestAborted));
        });
        sms.MapGet("/messages", (string? to, int? limit, OperationStore store, CancellationToken cancellationToken) =>
            ListAsync(store, Providers.Sms, Mobile.ToE164(to), limit, cancellationToken));

        var email = endpoints.MapGroup("/email/v1").WithTags("Email gateway").RequireClientKey();
        email.MapPost("/messages", (EmailRequest request, HttpContext context, FaultInjector faults, OperationStore store, TimeProvider clock) =>
        {
            if (Invalid(request.MessageId) is { } badId)
            {
                return Task.FromResult(badId);
            }

            if (request.To is not { Length: <= 254 } to || !EmailAddress().IsMatch(to))
            {
                return Task.FromResult(MockErrors.Invalid("email.invalid_address", "to must be an email address.").ToProblem());
            }

            if (request.Subject is not { Length: > 0 and <= 200 } subject || request.Body is not { Length: > 0 and <= 10_000 } body)
            {
                return Task.FromResult(MockErrors.Invalid("email.invalid_message", "subject (at most 200 characters) and body (at most 10,000) are required.").ToProblem());
            }

            return faults.RunAsync(context, [Providers.Email], () =>
                AcceptAsync(store, clock, Providers.Email, EmailKind, request.MessageId!, to.ToLowerInvariant(), subject, body, request, context.RequestAborted));
        });
        email.MapGet("/messages", (string? to, int? limit, OperationStore store, CancellationToken cancellationToken) =>
            ListAsync(store, Providers.Email, to?.Trim().ToLowerInvariant(), limit, cancellationToken));

        return endpoints;
    }

    private static IResult? Invalid(string? messageId) =>
        messageId is { Length: > 0 and <= 64 } ? null : MockErrors.Invalid("messaging.invalid_message_id", "messageId is required (at most 64 characters).").ToProblem();

    private static async Task<IResult> AcceptAsync(
        OperationStore store, TimeProvider clock, string provider, string kind, string messageId, string to, string? subject, string text, object request, CancellationToken cancellationToken)
    {
        var hash = Operation.HashOf(request);
        var now = clock.GetUtcNow();
        var (stored, created) = await store.AddOrGetAsync(
            new Operation
            {
                Provider = provider,
                ClientId = messageId,
                Kind = kind,
                Reference = $"{kind.ToUpperInvariant()}{Guid.NewGuid():N}"[..24].ToUpperInvariant(),
                RequestHash = hash,
                Counterparty = to,
                SettlementParty = provider,
                Status = OperationStatus.Completed,
                Details = JsonSerializer.Serialize(new MessageDetails(subject, text), JsonSerializerOptions.Web),
                CreatedAt = now.UtcDateTime,
                CompletedAt = now.UtcDateTime,
            },
            cancellationToken);

        // The same ID with a different message is a caller bug, not a retry.
        return created || stored.RequestHash == hash ? Results.Ok(View(stored)) : MockErrors.DuplicateId("messageId").ToProblem();
    }

    private static async Task<IResult> ListAsync(OperationStore store, string provider, string? to, int? limit, CancellationToken cancellationToken)
    {
        if (to is null)
        {
            return MockErrors.Invalid("messaging.to_required", "to is required.").ToProblem();
        }

        var messages = await store.RecentToAsync(provider, to, Math.Clamp(limit ?? 20, 1, 100), cancellationToken);
        return Results.Ok(messages.Select(View));
    }

    private static SentMessage View(Operation message)
    {
        var details = message.DetailsAs<MessageDetails>();
        return new(
            message.ClientId,
            message.Reference,
            message.Kind,
            message.Counterparty,
            details.Subject,
            details.Text,
            new DateTimeOffset(DateTime.SpecifyKind(message.CreatedAt, DateTimeKind.Utc)));
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailAddress();
}

internal sealed record MessageDetails(string? Subject, string Text);
