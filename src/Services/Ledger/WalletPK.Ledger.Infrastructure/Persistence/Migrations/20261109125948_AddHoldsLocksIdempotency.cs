using System;
using Microsoft.EntityFrameworkCore.Migrations;
using WalletPK.Ledger.Infrastructure.Persistence.Sql;

#nullable disable

namespace WalletPK.Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldsLocksIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "lock_token",
                table: "accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    captured_amount = table.Column<long>(type: "bigint", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    capture_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_holds", x => x.id);
                    table.CheckConstraint("ck_holds_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_holds_captured_within_amount", "captured_amount >= 0 AND captured_amount <= amount");
                    table.CheckConstraint("ck_holds_status", "status IN ('Active', 'Captured', 'Released', 'Expired')");
                    table.ForeignKey(
                        name: "fk_holds_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    location = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    response_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => x.key);
                    table.CheckConstraint("ck_idempotency_keys_state", "state IN ('InProgress', 'Completed')");
                });

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("0460de94-4068-7906-8e58-a3b89eb3bc0e"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("09e16b17-1fdb-3646-f691-82d14c85e39a"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("36255c8b-bb1f-a81f-5151-642cd4d60b69"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("483953dc-1820-71a3-ebe1-0e5db2e60bce"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("5e50d554-0dfc-5d9b-20f7-db365af9fe40"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("5e9c7f56-e61f-8783-b67c-049f87f31554"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("5f04c026-5f01-e7ee-3433-9a36685994cf"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("675b73ed-a49b-7990-e60e-d13110dd3a97"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("6c7493fc-056d-0fa1-fa56-be13778faa3c"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("74a79ec5-997d-f5a8-8faa-91f969b36bc0"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("7b47bf82-ebf4-b015-3e1d-badf7c16226f"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("94853e65-858c-f8cc-faf7-4284c1ef8779"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("96776cf5-368d-3d70-291c-f072580ab20f"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("9a8c94f6-ea1a-2f99-9b7b-ec7fc25d91a9"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("9b11b90c-57e3-4275-d77e-e5a044c55b93"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("b46fa951-41f3-62f7-7f1e-9a16f8fdc163"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("d8a99960-3339-ba14-e90a-3aaaa69ffab8"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("dc1dcb0f-9224-0187-37e7-f08c733c435d"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("e7523bde-cd45-2963-bbfc-ee8e1ada7d6a"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("f8ea7e75-2684-8d28-276d-75924329de3e"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("fcb0de6f-989b-2f2d-be5e-bc008f0295ff"),
                column: "lock_token",
                value: 0L);

            migrationBuilder.CreateIndex(
                name: "ix_holds_account_id",
                table: "holds",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_holds_active_expires_at",
                table: "holds",
                column: "expires_at",
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_expires_at",
                table: "idempotency_keys",
                column: "expires_at");

            // M3.6: the database refuses a write from a wallet lock that has already been superseded.
            migrationBuilder.Sql(LedgerDatabaseGuards.InstallFencing);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LedgerDatabaseGuards.UninstallFencing);

            migrationBuilder.DropTable(
                name: "holds");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "accounts");
        }
    }
}
