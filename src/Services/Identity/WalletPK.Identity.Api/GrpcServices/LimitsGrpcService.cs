using Grpc.Core;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Contracts.Identity.V1;
using WalletPK.Identity.Application.Limits;
using WalletPK.Identity.Domain.Devices;
using ProtoDecision = WalletPK.Contracts.Identity.V1.LimitDecision;
using ProtoTransactionType = WalletPK.Contracts.Identity.V1.TransactionType;

namespace WalletPK.Identity.Api.GrpcServices;

/// <summary>
/// gRPC face of <see cref="LimitService"/> for other services (the Ledger in M3). Internal only:
/// it listens on the gRPC port, which the API gateway never routes to. Bad input is answered with
/// INVALID_ARGUMENT; a refused debit is a normal reply with a decision, not an error.
/// </summary>
public sealed class LimitsGrpcService(LimitService limits) : Contracts.Identity.V1.Limits.LimitsBase
{
    public override async Task<LimitResponse> CheckLimit(CheckLimitRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var limitRequest = ToLimitRequest(request.UserId, request.AmountPaisa, request.TransactionType, request.DeviceId);
        return ToResponse(await limits.CheckAsync(limitRequest, context.CancellationToken));
    }

    public override async Task<LimitResponse> ReserveLimit(ReserveLimitRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var limitRequest = ToLimitRequest(request.UserId, request.AmountPaisa, request.TransactionType, request.DeviceId);
        return ToResponse(await limits.ReserveAsync(request.TransactionId, limitRequest, context.CancellationToken));
    }

    public override async Task<ReleaseLimitResponse> ReleaseLimit(ReleaseLimitRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(request.TransactionId))
        {
            throw InvalidArgument(LimitService.TransactionIdRequired.Message);
        }

        return new ReleaseLimitResponse { Released = await limits.ReleaseAsync(request.TransactionId, context.CancellationToken) };
    }

    private static LimitRequest ToLimitRequest(string userId, long amountPaisa, ProtoTransactionType type, string deviceId)
    {
        if (!UserId.TryParse(userId, null, out var parsedUserId))
        {
            throw InvalidArgument("user_id must be a UUID.");
        }

        DeviceId? parsedDeviceId = null;
        if (!string.IsNullOrEmpty(deviceId))
        {
            parsedDeviceId = DeviceId.TryParse(deviceId, null, out var id) ? id : throw InvalidArgument("device_id must be a UUID when set.");
        }

        var debitType = type switch
        {
            ProtoTransactionType.P2PTransfer => DebitType.P2PTransfer,
            ProtoTransactionType.BankTransfer => DebitType.BankTransfer,
            ProtoTransactionType.BillPayment => DebitType.BillPayment,
            ProtoTransactionType.MobileTopup => DebitType.MobileTopup,
            ProtoTransactionType.MerchantPayment => DebitType.MerchantPayment,
            ProtoTransactionType.CashOut => DebitType.CashOut,
            _ => throw InvalidArgument("transaction_type must be set."),
        };

        return new LimitRequest(parsedUserId, Money.FromPaisa(amountPaisa), debitType, parsedDeviceId);
    }

    private static LimitResponse ToResponse(Result<LimitResult> result)
    {
        if (result.IsFailure)
        {
            throw InvalidArgument(result.Error.Message);
        }

        var value = result.Value;
        return new LimitResponse
        {
            Decision = value.Decision switch
            {
                Application.Limits.LimitDecision.Allowed => ProtoDecision.Allowed,
                Application.Limits.LimitDecision.ExceedsPerTransaction => ProtoDecision.ExceedsPerTransaction,
                Application.Limits.LimitDecision.ExceedsDaily => ProtoDecision.ExceedsDaily,
                Application.Limits.LimitDecision.ExceedsMonthly => ProtoDecision.ExceedsMonthly,
                Application.Limits.LimitDecision.CoolingOff => ProtoDecision.CoolingOff,
                _ => ProtoDecision.UserNotAllowed,
            },
            Reason = value.Reason,
            KycTier = value.KycTier?.ToString() ?? string.Empty,
            PerTransactionLimitPaisa = value.PerTransactionLimit.MinorUnits,
            DailyRemainingPaisa = value.DailyRemaining.MinorUnits,
            MonthlyRemainingPaisa = value.MonthlyRemaining.MinorUnits,
        };
    }

    private static RpcException InvalidArgument(string message) => new(new Status(StatusCode.InvalidArgument, message));
}
