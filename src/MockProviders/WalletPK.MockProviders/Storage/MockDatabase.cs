using Dapper;
using Npgsql;

namespace WalletPK.MockProviders.Storage;

/// <summary>
/// The mock providers' own database ("walletpk_mockproviders"): what each provider accepted, the
/// callbacks still to deliver, NADRA's citizen records and the planned settlement-file mismatches.
/// Plain SQL through Dapper; the schema is created at startup (it is a test double, not a product, so
/// no migrations history).
/// </summary>
public sealed class MockDatabase(NpgsqlDataSource dataSource)
{
    public const string ConnectionName = "MockProvidersDb";

    public NpgsqlDataSource DataSource { get; } = dataSource;

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) => DataSource.OpenConnectionAsync(cancellationToken);

    /// <summary>Creates the tables if they are missing. Safe to run from several instances at once.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(Schema, transaction: transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private const string Schema = """
        SELECT pg_advisory_xact_lock(5_200_001);

        CREATE SCHEMA IF NOT EXISTS mock;

        -- Everything a provider accepted: Raast transfers, bill payments, top-ups.
        -- (provider, client_id) is the caller's unique ID: the same ID twice is the same request.
        CREATE TABLE IF NOT EXISTS mock.operations (
            provider          text NOT NULL,
            client_id         text NOT NULL,
            kind              text NOT NULL,
            reference         text NOT NULL,
            request_hash      text NOT NULL,
            amount_paisa      bigint NOT NULL,
            counterparty      text NOT NULL,
            settlement_party  text NOT NULL,
            status            text NOT NULL,
            reason            text NULL,
            details           jsonb NOT NULL DEFAULT '{}'::jsonb,
            created_at        timestamptz NOT NULL,
            completed_at      timestamptz NULL,
            process_after     timestamptz NULL,
            PRIMARY KEY (provider, client_id)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_reference ON mock.operations (reference);
        CREATE INDEX IF NOT EXISTS ix_operations_due ON mock.operations (process_after) WHERE status = 'Pending';
        CREATE INDEX IF NOT EXISTS ix_operations_counterparty ON mock.operations (provider, counterparty, created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_operations_settlement ON mock.operations (settlement_party, completed_at) WHERE status = 'Completed';
        -- A bill can be paid once per billing month, even under concurrent payments.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_bill_paid
            ON mock.operations (provider, counterparty, (details->>'billingMonth'))
            WHERE kind = 'bill_payment' AND status = 'Completed';

        -- Signed callbacks waiting to be delivered (retried with back-off).
        CREATE TABLE IF NOT EXISTS mock.callbacks (
            id               bigserial PRIMARY KEY,
            provider         text NOT NULL,
            url              text NOT NULL,
            payload          text NOT NULL,
            attempts         int NOT NULL DEFAULT 0,
            next_attempt_at  timestamptz NOT NULL,
            delivered_at     timestamptz NULL,
            failed_at        timestamptz NULL,
            last_error       text NULL,
            created_at       timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_callbacks_due ON mock.callbacks (next_attempt_at) WHERE delivered_at IS NULL AND failed_at IS NULL;

        -- NADRA records added through the control API (others verify by the permissive rule).
        CREATE TABLE IF NOT EXISTS mock.citizens (
            cnic           text PRIMARY KEY,
            full_name      text NOT NULL,
            date_of_birth  date NOT NULL,
            status         text NOT NULL,
            updated_at     timestamptz NOT NULL
        );

        -- Mismatches to plant in a settlement file (scenario S8).
        CREATE TABLE IF NOT EXISTS mock.settlement_plans (
            party            text NOT NULL,
            business_date    date NOT NULL,
            missing          int NOT NULL,
            duplicate        int NOT NULL,
            amount_mismatch  int NOT NULL,
            unknown          int NOT NULL,
            PRIMARY KEY (party, business_date)
        );
        """;
}
