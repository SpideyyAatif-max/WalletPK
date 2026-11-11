using Npgsql;

namespace WalletPK.Ledger.Infrastructure.ReadModels;

/// <summary>
/// Connections for the read models (plain Npgsql + Dapper, no EF Core). The projection writes through
/// <see cref="Primary"/>; queries use <see cref="Reader"/>, which is the "LedgerReadDb" connection string
/// when set (a read replica) and otherwise the primary.
/// </summary>
public sealed class LedgerReadModelDatabase : IDisposable, IAsyncDisposable
{
    /// <summary>The tables' schema.</summary>
    public const string Schema = "read";

    /// <summary>The row in read.projection_state that holds this projection's generation.</summary>
    public const string ProjectionName = "ledger";

    public LedgerReadModelDatabase(string primaryConnectionString, string? readerConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryConnectionString);
        Primary = NpgsqlDataSource.Create(primaryConnectionString);
        Reader = string.IsNullOrWhiteSpace(readerConnectionString) || readerConnectionString == primaryConnectionString
            ? Primary
            : NpgsqlDataSource.Create(readerConnectionString);
    }

    public NpgsqlDataSource Primary { get; }

    public NpgsqlDataSource Reader { get; }

    public void Dispose()
    {
        if (!ReferenceEquals(Reader, Primary))
        {
            Reader.Dispose();
        }

        Primary.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!ReferenceEquals(Reader, Primary))
        {
            await Reader.DisposeAsync();
        }

        await Primary.DisposeAsync();
    }
}
