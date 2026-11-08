namespace WalletPK.Observability.UnitTests.Infrastructure;

/// <summary>
/// Serilog's request logging writes through the process-wide Log.Logger, so tests that each
/// start their own app share this collection: tests in one collection never run at the same time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerilogCollection
{
    public const string Name = "Serilog";
}
