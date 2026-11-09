using Grpc.Core;
using Microsoft.Extensions.Logging;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Contracts.Identity.V1;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Domain.Journals;
using ProtoTransactionType = WalletPK.Contracts.Identity.V1.TransactionType;

namespace WalletPK.Ledger.Infrastructure.Limits;

/// <summary>Where the Identity service's gRPC port is, from "Services:IdentityGrpc".</summary>
public sealed class IdentityGrpcOptions
{
    public const string SectionName = "Services:IdentityGrpc";

    /// <summary>For example http://localhost:5201 (dev) or http://identity:8081 (compose).</summary>
    public Uri? Address { get; set; }

    /// <summary>How long a limits call may take before the payment fails closed.</summary>
    public int TimeoutMilliseconds { get; set; } = 3_000;
}

/// <summary>
/// <see cref="ILimitsGateway"/> over the Identity service's Limits gRPC API (M2.6). Fails closed:
/// if Identity does not answer within the deadline, the payment is refused.
/// </summary>
internal sealed partial class IdentityLimitsGateway(
    Contracts.Identity.V1.Limits.LimitsClient client,
    IdentityGrpcOptions options,
    ILogger<IdentityLimitsGateway> logger) : ILimitsGateway
{
    public async Task<LimitDecisionResult> ReserveAsync(
        TransactionId transactionId, UserId userId, JournalType type, Money amount, string? deviceId, CancellationToken cancellationToken)
    {
        var request = new ReserveLimitRequest
        {
            TransactionId = transactionId.ToString(),
            UserId = userId.ToString(),
            AmountPaisa = amount.MinorUnits,
            TransactionType = ToProto(type),
            DeviceId = deviceId ?? string.Empty,
        };

        try
        {
            var response = await client.ReserveLimitAsync(request, deadline: Deadline(), cancellationToken: cancellationToken);
            var decision = response.Decision switch
            {
                LimitDecision.Allowed => "Allowed",
                LimitDecision.ExceedsPerTransaction => "ExceedsPerTransaction",
                LimitDecision.ExceedsDaily => "ExceedsDaily",
                LimitDecision.ExceedsMonthly => "ExceedsMonthly",
                LimitDecision.CoolingOff => "CoolingOff",
                LimitDecision.UserNotAllowed => "UserNotAllowed",
                _ => "Unknown",
            };
            return new LimitDecisionResult(response.Decision == LimitDecision.Allowed, decision, response.Reason);
        }
        catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled || !cancellationToken.IsCancellationRequested)
        {
            LogReserveFailed(logger, exception.StatusCode, transactionId, exception);
            throw new LimitsUnavailableException("The limits service did not answer.", exception);
        }
    }

    public async Task ReleaseAsync(TransactionId transactionId, CancellationToken cancellationToken)
    {
        try
        {
            await client.ReleaseLimitAsync(
                new ReleaseLimitRequest { TransactionId = transactionId.ToString() },
                deadline: Deadline(),
                cancellationToken: cancellationToken);
        }
        catch (RpcException exception)
        {
            // Best effort: a reservation that is never released only lowers today's remaining limit.
            LogReleaseFailed(logger, exception.StatusCode, transactionId, exception);
        }
    }

    private DateTime Deadline() => DateTime.UtcNow.AddMilliseconds(options.TimeoutMilliseconds);

    private static ProtoTransactionType ToProto(JournalType type) => type switch
    {
        JournalType.P2PTransfer => ProtoTransactionType.P2PTransfer,
        JournalType.BankTransfer => ProtoTransactionType.BankTransfer,
        JournalType.BillPayment => ProtoTransactionType.BillPayment,
        JournalType.MobileTopup => ProtoTransactionType.MobileTopup,
        JournalType.MerchantPayment => ProtoTransactionType.MerchantPayment,
        JournalType.CashOut => ProtoTransactionType.CashOut,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "This journal type is not a limited customer debit."),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Limit reservation failed with {StatusCode} for transaction {TransactionId}; refusing the payment")]
    private static partial void LogReserveFailed(ILogger logger, StatusCode statusCode, TransactionId transactionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Limit release failed with {StatusCode} for transaction {TransactionId}")]
    private static partial void LogReleaseFailed(ILogger logger, StatusCode statusCode, TransactionId transactionId, Exception exception);
}
