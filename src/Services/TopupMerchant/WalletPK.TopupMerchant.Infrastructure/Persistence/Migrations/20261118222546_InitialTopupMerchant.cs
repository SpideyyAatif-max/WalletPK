using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WalletPK.TopupMerchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTopupMerchant : Migration
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
                name: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fee_plan = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    city = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    wallet_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merchants", x => x.id);
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
                name: "merchant_qr_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_paisa = table.Column<long>(type: "bigint", nullable: false),
                    reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merchant_qr_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_merchant_qr_codes_merchants_merchant_id",
                        column: x => x.merchant_id,
                        principalTable: "merchants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "merchant_settlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    payment_count = table.Column<int>(type: "integer", nullable: false),
                    gross_paisa = table.Column<long>(type: "bigint", nullable: false),
                    fee_paisa = table.Column<long>(type: "bigint", nullable: false),
                    tax_paisa = table.Column<long>(type: "bigint", nullable: false),
                    net_paisa = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merchant_settlements", x => x.id);
                    table.ForeignKey(
                        name: "fk_merchant_settlements_merchants_merchant_id",
                        column: x => x.merchant_id,
                        principalTable: "merchants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
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

            migrationBuilder.CreateTable(
                name: "settled_payments",
                columns: table => new
                {
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settlement_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settled_payments", x => x.payment_id);
                    table.ForeignKey(
                        name: "fk_settled_payments_merchant_settlements_settlement_id",
                        column: x => x.settlement_id,
                        principalTable: "merchant_settlements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_at",
                table: "inbox_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "ix_merchant_qr_codes_merchant_id_created_at",
                table: "merchant_qr_codes",
                columns: new[] { "merchant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_merchant_settlements_merchant_id_business_date",
                table: "merchant_settlements",
                columns: new[] { "merchant_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_merchant_settlements_status",
                table: "merchant_settlements",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_merchants_code",
                table: "merchants",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_merchants_owner_user_id",
                table: "merchants",
                column: "owner_user_id",
                unique: true);

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
                name: "ix_settled_payments_settlement_id",
                table: "settled_payments",
                column: "settlement_id");

            // "My top-ups", "my QR payments", "payments to my shop", and the unsettled sales the settlement takes.
            migrationBuilder.Sql("""
                CREATE INDEX ix_saga_instances_topup_user
                    ON saga_instances ((data->>'userId'), created_at DESC)
                 WHERE saga_type = 'topup';

                CREATE INDEX ix_saga_instances_merchant_payment_payer
                    ON saga_instances ((data->>'payerUserId'), created_at DESC)
                 WHERE saga_type = 'merchant-payment';

                CREATE INDEX ix_saga_instances_merchant_payment_merchant
                    ON saga_instances ((data->>'merchantId'), created_at DESC)
                 WHERE saga_type = 'merchant-payment';

                CREATE INDEX ix_saga_instances_merchant_payment_completed
                    ON saga_instances (completed_at)
                 WHERE saga_type = 'merchant-payment' AND state = 'Completed';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_saga_instances_merchant_payment_completed;
                DROP INDEX IF EXISTS ix_saga_instances_merchant_payment_merchant;
                DROP INDEX IF EXISTS ix_saga_instances_merchant_payment_payer;
                DROP INDEX IF EXISTS ix_saga_instances_topup_user;
                """);

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "merchant_qr_codes");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "saga_steps");

            migrationBuilder.DropTable(
                name: "settled_payments");

            migrationBuilder.DropTable(
                name: "saga_instances");

            migrationBuilder.DropTable(
                name: "merchant_settlements");

            migrationBuilder.DropTable(
                name: "merchants");
        }
    }
}
