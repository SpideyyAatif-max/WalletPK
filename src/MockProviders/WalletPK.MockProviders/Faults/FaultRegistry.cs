using System.Collections.Concurrent;

namespace WalletPK.MockProviders.Faults;

/// <summary>
/// The faults in force, per provider. Kept in memory: faults are a test-time switch, and a restart (like
/// a real provider recovering) clears them.
/// </summary>
public sealed class FaultRegistry(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, ActiveFaults> _faults = new(StringComparer.Ordinal);

    public ActiveFaults Set(string provider, FaultSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var now = timeProvider.GetUtcNow();
        var active = new ActiveFaults(provider, settings, now, settings.DurationSeconds is { } seconds ? now.AddSeconds(seconds) : null);
        _faults[provider] = active;
        return active;
    }

    /// <summary>The faults for a provider, or null when it behaves normally (or its faults expired).</summary>
    public FaultSettings? For(string provider)
    {
        if (!_faults.TryGetValue(provider, out var active))
        {
            return null;
        }

        if (active.ExpiresAt is { } expires && expires <= timeProvider.GetUtcNow())
        {
            _faults.TryRemove(new KeyValuePair<string, ActiveFaults>(provider, active));
            return null;
        }

        return active.Settings;
    }

    public IReadOnlyList<ActiveFaults> All()
    {
        var now = timeProvider.GetUtcNow();
        return [.. _faults.Values.Where(active => active.ExpiresAt is not { } expires || expires > now).OrderBy(active => active.Provider, StringComparer.Ordinal)];
    }

    public bool Clear(string provider) => _faults.TryRemove(provider, out _);

    public void ClearAll() => _faults.Clear();
}
