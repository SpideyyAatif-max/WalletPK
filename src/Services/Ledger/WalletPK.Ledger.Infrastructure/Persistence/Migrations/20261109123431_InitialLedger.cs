using System;
using Microsoft.EntityFrameworkCore.Migrations;
using WalletPK.Ledger.Infrastructure.Persistence.Sql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace WalletPK.Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    system_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    shard_index = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    normal_balance = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    allow_negative = table.Column<bool>(type: "boolean", nullable: false),
                    balance = table.Column<long>(type: "bigint", nullable: false),
                    held = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_available_non_negative", "allow_negative OR balance - held >= 0");
                    table.CheckConstraint("ck_accounts_held_non_negative", "held >= 0");
                    table.CheckConstraint("ck_accounts_owner_or_system_code", "(type = 'System' AND owner_id IS NULL AND system_code IS NOT NULL) OR (type <> 'System' AND owner_id IS NOT NULL AND system_code IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "journal_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_transactions", x => x.id);
                    table.CheckConstraint("ck_journal_transactions_amount_positive", "amount > 0");
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
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<short>(type: "smallint", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    balance_after = table.Column<long>(type: "bigint", nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entries", x => x.id);
                    table.CheckConstraint("ck_journal_entries_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_journal_entries_direction", "direction IN ('Debit', 'Credit')");
                    table.ForeignKey(
                        name: "fk_journal_entries_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_journal_entries_journal_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "journal_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "accounts",
                columns: new[] { "id", "allow_negative", "balance", "created_at", "currency", "held", "name", "normal_balance", "owner_id", "shard_index", "status", "system_code", "type", "updated_at" },
                values: new object[,]
                {
                    { new Guid("0460de94-4068-7906-8e58-a3b89eb3bc0e"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Biller settlement (shard 0)", "Credit", null, 0, "Active", "BILLER_SETTLEMENT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("09e16b17-1fdb-3646-f691-82d14c85e39a"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Fee income (shard 1)", "Credit", null, 1, "Active", "FEE_INCOME", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("36255c8b-bb1f-a81f-5151-642cd4d60b69"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Fee income (shard 3)", "Credit", null, 3, "Active", "FEE_INCOME", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("483953dc-1820-71a3-ebe1-0e5db2e60bce"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Fee income (shard 5)", "Credit", null, 5, "Active", "FEE_INCOME", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("5e50d554-0dfc-5d9b-20f7-db365af9fe40"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Fee income (shard 2)", "Credit", null, 2, "Active", "FEE_INCOME", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("5e9c7f56-e61f-8783-b67c-049f87f31554"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Fee income (shard 6)", "Credit", null, 6, "Active", "FEE_INCOME", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("5f04c026-5f01-e7ee-3433-9a36685994cf"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Biller settlement (shard 1)", "Credit", null, 1, "Active", "BILLER_SETTLEMENT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("675b73ed-a49b-7990-e60e-d13110dd3a97"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Suspense", "Debit", null, 0, "Active", "SUSPENSE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("6c7493fc-056d-0fa1-fa56-be13778faa3c"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Biller settlement (shard 3)", "Credit", null, 3, "Active", "BILLER_SETTLEMENT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("74a79ec5-997d-f5a8-8faa-91f969b36bc0"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Tax payable (shard 0)", "Credit", null, 0, "Active", "TAX_PAYABLE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("7b47bf82-ebf4-b015-3e1d-badf7c16226f"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Bank float (shard 2)", "Debit", null, 2, "Active", "BANK_FLOAT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("94853e65-858c-f8cc-faf7-4284c1ef8779"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Bank float (shard 3)", "Debit", null, 3, "Active", "BANK_FLOAT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("96776cf5-368d-3d70-291c-f072580ab20f"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Fee income (shard 4)", "Credit", null, 4, "Active", "FEE_INCOME", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("9a8c94f6-ea1a-2f99-9b7b-ec7fc25d91a9"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Tax payable (shard 3)", "Credit", null, 3, "Active", "TAX_PAYABLE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("9b11b90c-57e3-4275-d77e-e5a044c55b93"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Bank float (shard 1)", "Debit", null, 1, "Active", "BANK_FLOAT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("b46fa951-41f3-62f7-7f1e-9a16f8fdc163"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Fee income (shard 0)", "Credit", null, 0, "Active", "FEE_INCOME", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("d8a99960-3339-ba14-e90a-3aaaa69ffab8"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Tax payable (shard 2)", "Credit", null, 2, "Active", "TAX_PAYABLE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("dc1dcb0f-9224-0187-37e7-f08c733c435d"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Bank float (shard 0)", "Debit", null, 0, "Active", "BANK_FLOAT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("e7523bde-cd45-2963-bbfc-ee8e1ada7d6a"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Tax payable (shard 1)", "Credit", null, 1, "Active", "TAX_PAYABLE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("f8ea7e75-2684-8d28-276d-75924329de3e"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Fee income (shard 7)", "Credit", null, 7, "Active", "FEE_INCOME", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("fcb0de6f-989b-2f2d-be5e-bc008f0295ff"), true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, "Biller settlement (shard 2)", "Credit", null, 2, "Active", "BILLER_SETTLEMENT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });

            migrationBuilder.CreateIndex(
                name: "ux_accounts_owner_type",
                table: "accounts",
                columns: new[] { "owner_id", "type" },
                unique: true,
                filter: "owner_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_accounts_system_code_shard",
                table: "accounts",
                columns: new[] { "system_code", "shard_index" },
                unique: true,
                filter: "system_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_account_history",
                table: "journal_entries",
                columns: new[] { "account_id", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_journal_entries_transaction_line",
                table: "journal_entries",
                columns: new[] { "transaction_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_transactions_posted_at",
                table: "journal_transactions",
                column: "posted_at");

            migrationBuilder.CreateIndex(
                name: "ix_journal_transactions_reference",
                table: "journal_transactions",
                column: "reference");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unpublished",
                table: "outbox_messages",
                column: "created_at",
                filter: "published_at IS NULL");

            // M3.2: the database's own guards (balanced journals, append-only history). Added by hand:
            // EF Core models tables and columns, not triggers.
            migrationBuilder.Sql(LedgerDatabaseGuards.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LedgerDatabaseGuards.Uninstall);

            migrationBuilder.DropTable(
                name: "journal_entries");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "journal_transactions");
        }
    }
}
