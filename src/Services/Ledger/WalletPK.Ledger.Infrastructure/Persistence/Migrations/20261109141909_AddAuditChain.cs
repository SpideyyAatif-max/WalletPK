using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using WalletPK.Ledger.Infrastructure.Persistence.Sql;

#nullable disable

namespace WalletPK.Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    payload = table.Column<string>(type: "text", nullable: false),
                    chain_sequence = table.Column<long>(type: "bigint", nullable: true),
                    previous_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    sealed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                    table.CheckConstraint("ck_audit_log_chain_sequence_positive", "chain_sequence IS NULL OR chain_sequence > 0");
                    table.CheckConstraint("ck_audit_log_sealed_consistently", "(chain_sequence IS NULL AND previous_hash IS NULL AND hash IS NULL AND sealed_at IS NULL) OR (chain_sequence IS NOT NULL AND previous_hash IS NOT NULL AND hash IS NOT NULL AND sealed_at IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_subject",
                table: "audit_log",
                columns: new[] { "subject_type", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_unsealed",
                table: "audit_log",
                column: "id",
                filter: "hash IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_audit_log_chain_sequence",
                table: "audit_log",
                column: "chain_sequence",
                unique: true,
                filter: "chain_sequence IS NOT NULL");

            // Hand-added: append-only guard, then one audit row for every journal posted before this migration.
            migrationBuilder.Sql(LedgerDatabaseGuards.InstallAudit);
            migrationBuilder.Sql(LedgerDatabaseGuards.BackfillAudit);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LedgerDatabaseGuards.UninstallAudit);

            migrationBuilder.DropTable(
                name: "audit_log");
        }
    }
}
