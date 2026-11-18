using WalletPK.TopupMerchant.Domain;

namespace WalletPK.TopupMerchant.Application.TopUps;

/// <summary>How a call to a network ended (see WalletPK.Resilience's ProviderOutcome).</summary>
public enum TelcoCallOutcome
{
    Ok,

    /// <summary>Refused (4xx): inactive number, wrong amount ... Trying again will not help.</summary>
    Rejected,

    /// <summary>Not known: (status query) the top-up never arrived.</summary>
    NotFound,

    /// <summary>Certainly not done: the network is down, its circuit is open, or its bulkhead is full.</summary>
    Unavailable,

    /// <summary>No answer: it may or may not have been done. Ask before trying again.</summary>
    Unknown,
}

public sealed record Bundle(string Code, string Name, long PricePaisa, int ValidityDays);

public sealed record TelcoReceipt(string TelcoReference, long AmountPaisa, string? BundleCode, DateTimeOffset CompletedAt);

public sealed record TopUpCallResult(TelcoCallOutcome Outcome, TelcoReceipt? Receipt, string? Code, string? Detail);

/// <summary>The mobile networks, each through its own resilience pipeline; no call throws for a failed call.</summary>
public interface ITelcoGateway
{
    Task<(TelcoCallOutcome Outcome, IReadOnlyList<Bundle> Bundles, string? Detail)> BundlesAsync(Network network, CancellationToken cancellationToken);

    /// <summary>Airtime (<paramref name="bundleCode"/> null) or a bundle; <paramref name="topUpId"/> makes a repeat the same top-up.</summary>
    Task<TopUpCallResult> TopUpAsync(Network network, Guid topUpId, string msisdn, long amountPaisa, string? bundleCode, CancellationToken cancellationToken);

    /// <summary>The status query: did top-up <paramref name="topUpId"/> arrive?</summary>
    Task<TopUpCallResult> GetTopUpAsync(Network network, Guid topUpId, CancellationToken cancellationToken);
}

public sealed record NetworkAvailability(string Code, string Name, IReadOnlyList<string> Prefixes, string Status, DateTimeOffset? Since);

public interface INetworkAvailability
{
    IReadOnlyList<NetworkAvailability> GetAll();
}
