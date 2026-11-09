using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WalletPK.Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "alias",
                table: "accounts",
                type: "character varying(13)",
                maxLength: 13,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("0460de94-4068-7906-8e58-a3b89eb3bc0e"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("09e16b17-1fdb-3646-f691-82d14c85e39a"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("36255c8b-bb1f-a81f-5151-642cd4d60b69"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("483953dc-1820-71a3-ebe1-0e5db2e60bce"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("5e50d554-0dfc-5d9b-20f7-db365af9fe40"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("5e9c7f56-e61f-8783-b67c-049f87f31554"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("5f04c026-5f01-e7ee-3433-9a36685994cf"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("675b73ed-a49b-7990-e60e-d13110dd3a97"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("6c7493fc-056d-0fa1-fa56-be13778faa3c"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("74a79ec5-997d-f5a8-8faa-91f969b36bc0"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("7b47bf82-ebf4-b015-3e1d-badf7c16226f"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("94853e65-858c-f8cc-faf7-4284c1ef8779"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("96776cf5-368d-3d70-291c-f072580ab20f"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("9a8c94f6-ea1a-2f99-9b7b-ec7fc25d91a9"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("9b11b90c-57e3-4275-d77e-e5a044c55b93"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("b46fa951-41f3-62f7-7f1e-9a16f8fdc163"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("d8a99960-3339-ba14-e90a-3aaaa69ffab8"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("dc1dcb0f-9224-0187-37e7-f08c733c435d"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("e7523bde-cd45-2963-bbfc-ee8e1ada7d6a"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("f8ea7e75-2684-8d28-276d-75924329de3e"),
                column: "alias",
                value: null);

            migrationBuilder.UpdateData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("fcb0de6f-989b-2f2d-be5e-bc008f0295ff"),
                column: "alias",
                value: null);

            migrationBuilder.CreateIndex(
                name: "ux_accounts_alias",
                table: "accounts",
                column: "alias",
                unique: true,
                filter: "alias IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_accounts_alias",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "alias",
                table: "accounts");
        }
    }
}
