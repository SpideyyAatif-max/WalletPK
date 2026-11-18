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

    public static Error NetworkUnavailable(string? detail) => Error.Unavailable("topup.network_unavailable", $"The network could not be reached ({detail}). Try again in a moment.");
}

/// <summary>Mobile top-ups and bundles (M8.1).</summary>
public sealed class TopUpService(
    ISagaEngine engine,
    ISagaReader reader,
    ISagaLists lists,
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
            withTimeline ? [.. saga.Steps.Select(step => new TimelineStep(step.Sequence, step.State, step.Detail, step.At))] : null);
    }
}
