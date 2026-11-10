using System;
using Microsoft.EntityFrameworkCore.Migrations;
using WalletPK.Ledger.Infrastructure.Persistence.Sql;

#nullable disable

namespace WalletPK.Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "account_sequence",
                table: "journal_entries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "entry_sequence",
                table: "accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

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

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("0460de94-4068-7906-8e58-a3b89eb3bc0e"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("09e16b17-1fdb-3646-f691-82d14c85e39a"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("36255c8b-bb1f-a81f-5151-642cd4d60b69"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("483953dc-1820-71a3-ebe1-0e5db2e60bce"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("5e50d554-0dfc-5d9b-20f7-db365af9fe40"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("5e9c7f56-e61f-8783-b67c-049f87f31554"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("5f04c026-5f01-e7ee-3433-9a36685994cf"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("675b73ed-a49b-7990-e60e-d13110dd3a97"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("6c7493fc-056d-0fa1-fa56-be13778faa3c"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("74a79ec5-997d-f5a8-8faa-91f969b36bc0"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("7b47bf82-ebf4-b015-3e1d-badf7c16226f"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("94853e65-858c-f8cc-faf7-4284c1ef8779"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("96776cf5-368d-3d70-291c-f072580ab20f"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("9a8c94f6-ea1a-2f99-9b7b-ec7fc25d91a9"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("9b11b90c-57e3-4275-d77e-e5a044c55b93"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("b46fa951-41f3-62f7-7f1e-9a16f8fdc163"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("d8a99960-3339-ba14-e90a-3aaaa69ffab8"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("dc1dcb0f-9224-0187-37e7-f08c733c435d"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("e7523bde-cd45-2963-bbfc-ee8e1ada7d6a"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("f8ea7e75-2684-8d28-276d-75924329de3e"),
                column: "entry_sequence",
                value: 0L);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("fcb0de6f-989b-2f2d-be5e-bc008f0295ff"),
                column: "entry_sequence",
                value: 0L);

            // Hand-added: number the entries posted before this migration (1, 2, 3 ... per account) and set
            // each account's counter. Must run after the seeded rows above and before the unique index.
            migrationBuilder.Sql(LedgerDatabaseGuards.BackfillEntrySequences);

            migrationBuilder.CreateIndex(
                name: "ux_journal_entries_account_sequence",
                table: "journal_entries",
                columns: new[] { "account_id", "account_sequence" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_journal_entries_account_sequence_positive",
                table: "journal_entries",
                sql: "account_sequence > 0");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_at",
                table: "inbox_messages",
                column: "processed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropIndex(
                name: "ux_journal_entries_account_sequence",
                table: "journal_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_journal_entries_account_sequence_positive",
                table: "journal_entries");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "account_sequence",
                table: "journal_entries");

            migrationBuilder.DropColumn(
                name: "entry_sequence",
                table: "accounts");
        }
    }
}
