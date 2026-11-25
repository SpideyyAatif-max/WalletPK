using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WalletPK.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    details = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    previous_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.sequence);
                });

            migrationBuilder.CreateTable(
                name: "external_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    party = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    amount_paisa = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    service_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_payments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => new { x.event_id, x.consumer });
                });

            migrationBuilder.CreateTable(
                name: "recon_breaks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    party = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    our_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    client_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    our_amount_paisa = table.Column<long>(type: "bigint", nullable: true),
                    provider_amount_paisa = table.Column<long>(type: "bigint", nullable: true),
                    lines_in_file = table.Column<int>(type: "integer", nullable: false),
                    our_status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    resolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    first_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    found_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recon_breaks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settlement_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    party = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    total_paisa = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settlement_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recon_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    party = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    problem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lines = table.Column<int>(type: "integer", nullable: false),
                    matched = table.Column<int>(type: "integer", nullable: false),
                    provider_total_paisa = table.Column<long>(type: "bigint", nullable: false),
                    our_count = table.Column<int>(type: "integer", nullable: false),
                    our_total_paisa = table.Column<long>(type: "bigint", nullable: false),
                    missing_at_provider = table.Column<int>(type: "integer", nullable: false),
                    unknown_to_us = table.Column<int>(type: "integer", nullable: false),
                    duplicate = table.Column<int>(type: "integer", nullable: false),
                    amount_mismatch = table.Column<int>(type: "integer", nullable: false),
                    settled_but_failed_here = table.Column<int>(type: "integer", nullable: false),
                    settled_but_pending_here = table.Column<int>(type: "integer", nullable: false),
                    triggered_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recon_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_recon_runs_settlement_files_file_id",
                        column: x => x.file_id,
                        principalTable: "settlement_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "settlement_lines",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount_paisa = table.Column<long>(type: "bigint", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settlement_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_settlement_lines_settlement_files_file_id",
                        column: x => x.file_id,
                        principalTable: "settlement_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_hash",
                table: "audit_log",
                column: "hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_subject_sequence",
                table: "audit_log",
                columns: new[] { "subject", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_external_payments_party_completed_on",
                table: "external_payments",
                columns: new[] { "party", "completed_on" },
                filter: "completed_on IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_external_payments_provider_reference",
                table: "external_payments",
                column: "provider_reference",
                filter: "provider_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_at",
                table: "inbox_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "ix_recon_breaks_party_business_date_kind_key",
                table: "recon_breaks",
                columns: new[] { "party", "business_date", "kind", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recon_breaks_status_business_date",
                table: "recon_breaks",
                columns: new[] { "status", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_recon_runs_business_date_party_started_at",
                table: "recon_runs",
                columns: new[] { "business_date", "party", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_recon_runs_file_id",
                table: "recon_runs",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_settlement_files_party_business_date_fetched_at",
                table: "settlement_files",
                columns: new[] { "party", "business_date", "fetched_at" });

            migrationBuilder.CreateIndex(
                name: "ix_settlement_lines_file_id",
                table: "settlement_lines",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_settlement_lines_party_business_date_client_id",
                table: "settlement_lines",
                columns: new[] { "party", "business_date", "client_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "external_payments");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "recon_breaks");

            migrationBuilder.DropTable(
                name: "recon_runs");

            migrationBuilder.DropTable(
                name: "settlement_lines");

            migrationBuilder.DropTable(
                name: "settlement_files");
        }
    }
}
