using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WalletPK.Bills.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    event_version = table.Column<int>(type: "integer", nullable: false),
                    topic = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    partition_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    causation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    publish_attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saga_instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    saga_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    is_completed = table.Column<bool>(type: "boolean", nullable: false),
                    wake_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    wake_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    step_count = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saga_instances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saved_billers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    biller = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    consumer_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    nickname = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    customer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_billing_month = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    last_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    last_payable_paisa = table.Column<long>(type: "bigint", nullable: true),
                    last_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reminded_for_month = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_billers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saga_steps",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    saga_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    previous_state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    trigger = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saga_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_saga_steps_saga_instances",
                        column: x => x.saga_id,
                        principalTable: "saga_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_at",
                table: "inbox_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unpublished",
                table: "outbox_messages",
                column: "created_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_saga_instances_type_state",
                table: "saga_instances",
                columns: new[] { "saga_type", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_saga_instances_wake_at",
                table: "saga_instances",
                column: "wake_at",
                filter: "wake_at IS NOT NULL AND NOT is_completed");

            migrationBuilder.CreateIndex(
                name: "ux_saga_steps_saga_sequence",
                table: "saga_steps",
                columns: new[] { "saga_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_saved_billers_next_check_at",
                table: "saved_billers",
                column: "next_check_at");

            migrationBuilder.CreateIndex(
                name: "ix_saved_billers_user_id_biller_consumer_number",
                table: "saved_billers",
                columns: new[] { "user_id", "biller", "consumer_number" },
                unique: true);

            // "My bill payments", and the operations queue (dead-lettered and pending-verification payments).
            migrationBuilder.Sql("""
                CREATE INDEX ix_saga_instances_bill_payment_user
                    ON saga_instances ((data->>'userId'), created_at DESC)
                 WHERE saga_type = 'bill-payment';

                CREATE INDEX ix_saga_instances_bill_payment_attention
                    ON saga_instances (state, updated_at)
                 WHERE saga_type = 'bill-payment' AND state IN ('DeadLettered', 'PendingVerification');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_saga_instances_bill_payment_attention; DROP INDEX IF EXISTS ix_saga_instances_bill_payment_user;");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "saga_steps");

            migrationBuilder.DropTable(
                name: "saved_billers");

            migrationBuilder.DropTable(
                name: "saga_instances");
        }
    }
}
