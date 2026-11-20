using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace WalletPK.Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentCommissionAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "accounts",
                columns: new[] { "id", "alias", "allow_negative", "balance", "created_at", "currency", "entry_sequence", "held", "lock_token", "name", "normal_balance", "owner_id", "shard_index", "status", "system_code", "type", "updated_at" },
                values: new object[,]
                {
                    { new Guid("32a46af8-d19d-d150-f214-008b3220be3f"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Commission payable (shard 1)", "Credit", null, 1, "Active", "COMMISSION_PAYABLE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("33aa9d3d-8986-0808-fea0-967e091f798f"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Commission expense (shard 3)", "Debit", null, 3, "Active", "COMMISSION_EXPENSE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("440f8db0-9739-e420-a96c-39ea12884283"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Commission payable (shard 3)", "Credit", null, 3, "Active", "COMMISSION_PAYABLE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("46e78bbb-e274-3592-e443-dd71a23625a0"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Commission expense (shard 2)", "Debit", null, 2, "Active", "COMMISSION_EXPENSE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("5701b0c1-c8dd-4902-33ac-5771fabf3f47"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Commission expense (shard 0)", "Debit", null, 0, "Active", "COMMISSION_EXPENSE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("619f7c3e-fddb-d993-fb15-6286ba894d38"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Commission expense (shard 1)", "Debit", null, 1, "Active", "COMMISSION_EXPENSE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("7febca89-24bd-d272-6752-0d2784f1d0bf"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Commission payable (shard 0)", "Credit", null, 0, "Active", "COMMISSION_PAYABLE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("e2f75610-3a95-9129-4898-1c2a904edfe3"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Commission payable (shard 2)", "Credit", null, 2, "Active", "COMMISSION_PAYABLE", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("32a46af8-d19d-d150-f214-008b3220be3f"));

            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("33aa9d3d-8986-0808-fea0-967e091f798f"));

            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("440f8db0-9739-e420-a96c-39ea12884283"));

            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("46e78bbb-e274-3592-e443-dd71a23625a0"));

            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("5701b0c1-c8dd-4902-33ac-5771fabf3f47"));

            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("619f7c3e-fddb-d993-fb15-6286ba894d38"));

            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("7febca89-24bd-d272-6752-0d2784f1d0bf"));

            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("e2f75610-3a95-9129-4898-1c2a904edfe3"));
        }
    }
}
