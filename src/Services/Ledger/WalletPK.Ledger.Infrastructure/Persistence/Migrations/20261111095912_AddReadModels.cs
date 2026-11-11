using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WalletPK.Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReadModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The read side (M4.4): plain tables in their own schema, filled by the projection from
            // ledger.events and read with Dapper. Not part of the EF model on purpose: they can be emptied
            // and rebuilt from Kafka at any time (M4.6) and never hold the only copy of anything.
            migrationBuilder.Sql("""
                CREATE SCHEMA IF NOT EXISTS read;

                -- One row per projection; a rebuild bumps the generation (and the consumer group with it).
                CREATE TABLE read.projection_state (
                    name        text PRIMARY KEY,
                    generation  bigint NOT NULL,
                    rebuilt_at  timestamptz NULL
                );
                INSERT INTO read.projection_state (name, generation) VALUES ('ledger', 1);

                -- Each wallet's balance after the newest entry seen (last_sequence).
                CREATE TABLE read.wallet_balances (
                    wallet_id      uuid PRIMARY KEY,
                    owner_id       uuid NULL,
                    wallet_number  text NULL,
                    wallet_type    text NOT NULL,
                    balance_paisa  bigint NOT NULL,
                    last_sequence  bigint NOT NULL,
                    opened_at      timestamptz NULL,
                    updated_at     timestamptz NOT NULL
                );
                CREATE INDEX ix_wallet_balances_owner ON read.wallet_balances (owner_id, wallet_type);

                -- Every wallet entry, numbered per wallet: keyset paging walks the primary key backwards.
                CREATE TABLE read.transaction_history (
                    wallet_id                   uuid NOT NULL,
                    sequence                    bigint NOT NULL,
                    transaction_id              uuid NOT NULL,
                    journal_type                text NOT NULL,
                    description                 text NOT NULL,
                    direction                   text NOT NULL,
                    amount_paisa                bigint NOT NULL,
                    balance_after_paisa         bigint NOT NULL,
                    counterparty_wallet_number  text NULL,
                    posted_at                   timestamptz NOT NULL,
                    PRIMARY KEY (wallet_id, sequence)
                );
                CREATE INDEX ix_transaction_history_type ON read.transaction_history (wallet_id, journal_type, sequence);
                CREATE INDEX ix_transaction_history_posted_at ON read.transaction_history (wallet_id, posted_at);

                -- Journals already counted in the metrics, so a redelivered event is not counted twice.
                CREATE TABLE read.projected_journals (
                    transaction_id  uuid PRIMARY KEY,
                    journal_type    text NOT NULL,
                    amount_paisa    bigint NOT NULL,
                    posted_at       timestamptz NOT NULL
                );

                -- Journals and money moved per minute (UTC) and type.
                CREATE TABLE read.metrics_per_minute (
                    minute         timestamptz NOT NULL,
                    journal_type   text NOT NULL,
                    journal_count  bigint NOT NULL,
                    volume_paisa   bigint NOT NULL,
                    PRIMARY KEY (minute, journal_type)
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS read CASCADE;");
        }
    }
}
