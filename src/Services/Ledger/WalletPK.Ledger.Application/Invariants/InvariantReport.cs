namespace WalletPK.Ledger.Application.Invariants;

/// <summary>One rule the ledger must always satisfy, and whether it does right now.</summary>
/// <param name="Name">Stable machine name, for example "debits_equal_credits".</param>
/// <param name="Description">The rule in plain words.</param>
/// <param name="Passed">True when the rule holds.</param>
/// <param name="Violations">How many rows break it (0 when it holds).</param>
/// <param name="Detail">Numbers behind the result, or what is wrong.</param>
public sealed record InvariantCheck(string Name, string Description, bool Passed, long Violations, string Detail);

/// <summary>The result of one run of the invariant checker (M3.11).</summary>
/// <param name="CheckedAt">When the snapshot was taken.</param>
/// <param name="Healthy">True when every check passed.</param>
/// <param name="DurationMilliseconds">How long the run took.</param>
/// <param name="Checks">Every check, passed or not.</param>
public sealed record InvariantReport(DateTimeOffset CheckedAt, bool Healthy, long DurationMilliseconds, IReadOnlyList<InvariantCheck> Checks)
{
    public IEnumerable<InvariantCheck> Failures => Checks.Where(check => !check.Passed);
}

/// <summary>Proves the books are sound: run on a schedule, on demand by operations, and by the tests.</summary>
public interface ILedgerInvariantChecker
{
    Task<InvariantReport> CheckAsync(CancellationToken cancellationToken);
}
