using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WalletPK.Agents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    shop_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    city = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    area = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    wallet_id = table.Column<Guid>(type: "uuid", nullable: true),
                    low_float_paisa = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
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
                name: "cash_out_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_wallet_number = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    customer_device_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_paisa = table.Column<long>(type: "bigint", nullable: false),
                    fee_paisa = table.Column<long>(type: "bigint", nullable: false),
                    tax_paisa = table.Column<long>(type: "bigint", nullable: false),
                    commission_paisa = table.Column<long>(type: "bigint", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_out_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_cash_out_requests_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "commission_payouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    transaction_count = table.Column<int>(type: "integer", nullable: false),
                    gross_paisa = table.Column<long>(type: "bigint", nullable: false),
                    tax_paisa = table.Column<long>(type: "bigint", nullable: false),
                    net_paisa = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_payouts", x => x.id);
                    table.ForeignKey(
                        name: "fk_commission_payouts_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
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
                name: "paid_commissions",
                columns: table => new
                {
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payout_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_paid_commissions", x => x.transaction_id);
                    table.ForeignKey(
                        name: "fk_paid_commissions_commission_payouts_payout_id",
                        column: x => x.payout_id,
                        principalTable: "commission_payouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agents_code",
                table: "agents",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agents_owner_user_id",
                table: "agents",
                column: "owner_user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agents_status_city",
                table: "agents",
                columns: new[] { "status", "city" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_out_requests_agent_id",
                table: "cash_out_requests",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_cash_out_requests_customer_user_id_created_at",
                table: "cash_out_requests",
                columns: new[] { "customer_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_out_requests_customer_wallet_number_agent_id",
                table: "cash_out_requests",
                columns: new[] { "customer_wallet_number", "agent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_commission_payouts_agent_id_business_date",
                table: "commission_payouts",
                columns: new[] { "agent_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_commission_payouts_status",
                table: "commission_payouts",
                column: "status");

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
                name: "ix_paid_commissions_payout_id",
                table: "paid_commissions",
                column: "payout_id");

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

            // "My transactions" for agents and customers, and the unpaid commissions the payout takes.
            migrationBuilder.Sql("""
                CREATE INDEX ix_saga_instances_agent_transaction_agent
                    ON saga_instances ((data->>'agentId'), created_at DESC)
                 WHERE saga_type = 'agent-transaction';

                CREATE INDEX ix_saga_instances_agent_transaction_customer
                    ON saga_instances ((data->>'customerUserId'), created_at DESC)
                 WHERE saga_type = 'agent-transaction';

                CREATE INDEX ix_saga_instances_agent_transaction_completed
                    ON saga_instances (completed_at)
                 WHERE saga_type = 'agent-transaction' AND state = 'Completed';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_saga_instances_agent_transaction_completed;
                DROP INDEX IF EXISTS ix_saga_instances_agent_transaction_customer;
                DROP INDEX IF EXISTS ix_saga_instances_agent_transaction_agent;
                """);

            migrationBuilder.DropTable(
                name: "cash_out_requests");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "paid_commissions");

            migrationBuilder.DropTable(
                name: "saga_steps");

            migrationBuilder.DropTable(
                name: "commission_payouts");

            migrationBuilder.DropTable(
                name: "saga_instances");

            migrationBuilder.DropTable(
                name: "agents");
        }
    }
}
