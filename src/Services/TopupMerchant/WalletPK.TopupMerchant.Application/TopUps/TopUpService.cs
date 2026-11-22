using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Sagas;
using WalletPK.TopupMerchant.Domain;

namespace WalletPK.TopupMerchant.Application.TopUps;

public sealed record TimelineStep(int Sequence, string State, string Detail, DateTimeOffset At);

public sealed record TopUpView(
    Guid Id,
    string Status,
    string Step,
    string Network,
    string NetworkName,
    string Msisdn,
    long AmountPaisa,
    string? BundleCode,
    string? BundleName,
    string? TelcoReference,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<TimelineStep>? Timeline);

public sealed record SentTopUp(TopUpView TopUp, bool Created);

/// <summary>A dead-lettered top-up, for operations (FR-4.3).</summary>
public sealed record TopUpDeadLetterView(
    Guid Id,
    Guid UserId,
    string Network,
    string Msisdn,
    long AmountPaisa,
    string? BundleCode,
    int Attempts,
    string? DeadLetteredFrom,
    string? Reason,
    DateTimeOffset? DeadLetteredAt,
    DateTimeOffset RequestedAt,
    bool CanRefund,
    bool CanConfirm,
    IReadOnlyList<TimelineStep> Timeline);

/// <summary>Port: lists of Sagas of one type, by a field of their data, newest first.</summary>
public interface ISagaLists
{
    Task<IReadOnlyList<SagaSnapshot<TData>>> ByFieldAsync<TData>(string sagaType, string field, string value, int limit, CancellationToken cancellationToken)
        where TData : class;
}

public static class TopUpErrors
{
    public static readonly Error InvalidNumber = Error.Validation("topup.invalid_number", "Enter a Pakistani mobile number, for example 0300 1234567.");
    public static readonly Error UnknownNetwork = Error.Validation("topup.unknown_network", "This number is not on Jazz, Zong, Telenor or Ufone.");
    public static readonly Error InvalidAirtime = Error.Validation("topup.invalid_amount", "Airtime is whole rupees from Rs. 20 to Rs. 5,000.");
    public static readonly Error InvalidBundle = Error.Validation("topup.invalid_bundle", "Give the bundle's code and its price.");
    public static readonly Error KeyReused = Error.Conflict("topup.idempotency_key_reused", "This Idempotency-Key was already used for a different top-up.");
    public static readonly Error NotFound = Error.NotFound("topup.not_found", "No such top-up.");

    public static Error NotResolvable(string state) =>
        Error.Conflict("topup.not_resolvable", $"The top-up is {state}; only dead-lettered top-ups can be replayed, refunded or confirmed.");

    public static readonly Error AlreadyApplied = Error.Conflict(
        "topup.already_applied", "The network applied this top-up; it cannot be refunded. Replay it to settle the wallet.");

    public static readonly Error BeingReturned = Error.Conflict(
        "topup.being_returned", "The money is already being returned; it cannot be confirmed. Replay it to finish the return.");

    public static readonly Error ReferenceRequired = Error.Validation("topup.reference_required", "Give the network's reference from its settlement file.");

    public static Error NetworkUnavailable(string? detail) => Error.Unavailable("topup.network_unavailable", $"The network could not be reached ({detail}). Try again in a moment.");
}

/// <summary>Mobile top-ups and bundles (M8.1).</summary>
public sealed class TopUpService(
    ISagaEngine engine,
    ISagaReader reader,
    ISagaLists lists,
    ISagaExplorer explorer,
    TopUpSaga definition,
    ITelcoGateway telcos,
    INetworkAvailability availability,
    TimeProvider timeProvider)
{
    public IReadOnlyList<NetworkAvailability> Networks() => availability.GetAll();

    /// <summary>Which network a number is on.</summary>
    public Result<Network> Detect(string? msisdn) =>
        !PakistaniMobileNumber.TryNormalize(msisdn, out var number) ? TopUpErrors.InvalidNumber
        : Domain.Networks.Of(number) is { } network ? network
        : TopUpErrors.UnknownNetwork;

    public async Task<Result<IReadOnlyList<Bundle>>> BundlesAsync(string? networkCode, CancellationToken cancellationToken)
    {
        if (Domain.Networks.ByCode(networkCode) is not { } network)
        {
            return TopUpErrors.UnknownNetwork;
        }

        var (outcome, bundles, detail) = await telcos.BundlesAsync(network, cancellationToken);
        return outcome == TelcoCallOutcome.Ok ? Result.Success(bundles) : TopUpErrors.NetworkUnavailable(detail);
    }

    /// <summary>
    /// Accepts a top-up and starts its Saga. Airtime (<paramref name="bundleCode"/> null) or a bundle at the
    /// price the customer saw (the network refuses any other, and the money then comes back).
    /// </summary>
    public async Task<Result<SentTopUp>> TopUpAsync(
        Guid userId,
        string? deviceId,
        string idempotencyKey,
        string? msisdn,
        long amountPaisa,
        string? bundleCode,
        string? bundleName,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (!PakistaniMobileNumber.TryNormalize(msisdn, out var number))
        {
            return TopUpErrors.InvalidNumber;
        }

        if (Domain.Networks.Of(number) is not { } network)
        {
            return TopUpErrors.UnknownNetwork;
        }

        var bundle = string.IsNullOrWhiteSpace(bundleCode) ? null : bundleCode.Trim().ToUpperInvariant();
        if (bundle is null && !TopUpRules.IsValidAirtime(amountPaisa))
        {
            return TopUpErrors.InvalidAirtime;
        }

        if (bundle is not null && (amountPaisa is < 100 or > 1_000_000 || bundle.Length > 40))
        {
            return TopUpErrors.InvalidBundle;
        }

        var id = SagaId("topup", userId, idempotencyKey);
        var data = new TopUpData
        {
            UserId = userId,
            DeviceId = deviceId,
            IdempotencyKey = idempotencyKey,
            Network = network.Code,
            Msisdn = number,
            AmountPaisa = amountPaisa,
            BundleCode = bundle,
            BundleName = bundle is null ? null : (string.IsNullOrWhiteSpace(bundleName) ? bundle : bundleName.Trim()[..Math.Min(bundleName.Trim().Length, 60)]),
            RequestedAt = timeProvider.GetUtcNow(),
        };

        var created = await engine.StartAsync(definition, id, data, correlationId, cancellationToken);
        var saga = await reader.FindAsync<TopUpData>(id, cancellationToken) ?? throw new InvalidOperationException($"Top-up {id} vanished.");
        if (!created && (saga.Data.UserId != userId || saga.Data.Msisdn != number || saga.Data.AmountPaisa != amountPaisa || saga.Data.BundleCode != bundle))
        {
            return TopUpErrors.KeyReused;
        }

        return new SentTopUp(ToView(saga, withTimeline: true), created);
    }

    public async Task<Result<TopUpView>> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var saga = await reader.FindAsync<TopUpData>(id, cancellationToken);
        return saga is null || saga.SagaType != TopUpSaga.SagaName || saga.Data.UserId != userId ? TopUpErrors.NotFound : ToView(saga, withTimeline: true);
    }

    public async Task<IReadOnlyList<TopUpView>> ListAsync(Guid userId, int? limit, CancellationToken cancellationToken) =>
        [.. (await lists.ByFieldAsync<TopUpData>(TopUpSaga.SagaName, "userId", userId.ToString(), Math.Clamp(limit ?? 20, 1, 100), cancellationToken)).Select(saga => ToView(saga, withTimeline: false))];

    // ---- Operations (FR-4.3) ----

    /// <summary>The dead-letter queue (topup.dlq), oldest first.</summary>
    public async Task<IReadOnlyList<TopUpDeadLetterView>> DeadLettersAsync(int? limit, CancellationToken cancellationToken)
    {
        var found = await explorer.SearchAsync(new SagaSearch(TopUpSaga.SagaName, TopUpStates.DeadLettered, Limit: Math.Clamp(limit ?? 50, 1, 200)), cancellationToken);
        var views = new List<TopUpDeadLetterView>();
        foreach (var summary in found)
        {
            if (await reader.FindAsync<TopUpData>(summary.Id, cancellationToken) is not { } saga)
            {
                continue;
            }

            var data = saga.Data;
            views.Add(new TopUpDeadLetterView(
                saga.Id, data.UserId, data.Network, data.Msisdn, data.AmountPaisa, data.BundleCode, data.Attempts, data.DeadLetteredFrom, data.DeadLetterReason,
                data.DeadLetteredAt, data.RequestedAt, TopUpSaga.CanRefund(data), TopUpSaga.CanConfirm(data), Timeline(saga)));
        }

        return [.. views.OrderBy(view => view.DeadLetteredAt)];
    }

    public Task<Result<TopUpView>> ReplayAsync(Guid id, string operatorName, CancellationToken cancellationToken) =>
        ResolveAsync(id, _ => null, new ReplayTopUp(operatorName), cancellationToken);

    public Task<Result<TopUpView>> RefundAsync(Guid id, string operatorName, string? reason, CancellationToken cancellationToken) =>
        ResolveAsync(
            id,
            data => TopUpSaga.CanRefund(data) ? null : TopUpErrors.AlreadyApplied,
            new RefundTopUp(operatorName, string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason.Trim()),
            cancellationToken);

    public Task<Result<TopUpView>> ConfirmAsync(Guid id, string operatorName, string? telcoReference, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(telcoReference)
            ? Task.FromResult<Result<TopUpView>>(TopUpErrors.ReferenceRequired)
            : ResolveAsync(id, data => TopUpSaga.CanConfirm(data) ? null : TopUpErrors.BeingReturned, new ConfirmTopUp(operatorName, telcoReference.Trim()), cancellationToken);

    private async Task<Result<TopUpView>> ResolveAsync(Guid id, Func<TopUpData, Error?> check, object message, CancellationToken cancellationToken)
    {
        var saga = await reader.FindAsync<TopUpData>(id, cancellationToken);
        if (saga is null || saga.SagaType != TopUpSaga.SagaName)
        {
            return TopUpErrors.NotFound;
        }

        if (saga.State != TopUpStates.DeadLettered)
        {
            return TopUpErrors.NotResolvable(saga.State);
        }

        if (check(saga.Data) is { } error)
        {
            return error;
        }

        await engine.HandleAsync(id, message, causationId: null, cancellationToken);
        return ToView((await reader.FindAsync<TopUpData>(id, cancellationToken))!, withTimeline: true);
    }

    private static IReadOnlyList<TimelineStep> Timeline(SagaSnapshot<TopUpData> saga) =>
        [.. saga.Steps.Select(step => new TimelineStep(step.Sequence, step.State, step.Detail, step.At))];

    /// <summary>The same inputs always give the same ID (a retry finds its Saga).</summary>
    public static Guid SagaId(string purpose, params object[] parts)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"walletpk:topup-merchant:{purpose}:{string.Join(':', parts)}"), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    internal static TopUpView ToView(SagaSnapshot<TopUpData> saga, bool withTimeline)
    {
        var data = saga.Data;
        return new TopUpView(
            saga.Id, data.Status, saga.State, data.Network, Domain.Networks.ByCode(data.Network)?.Name ?? data.Network, data.Msisdn, data.AmountPaisa, data.BundleCode, data.BundleName,
            data.TelcoReference, data.FailureCode, data.FailureReason, data.RequestedAt, saga.UpdatedAt, saga.CompletedAt,
            withTimeline ? Timeline(saga) : null);
    }
}
