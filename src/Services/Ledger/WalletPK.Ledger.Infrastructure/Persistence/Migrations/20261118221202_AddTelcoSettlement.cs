using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace WalletPK.Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTelcoSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "accounts",
                columns: new[] { "id", "alias", "allow_negative", "balance", "created_at", "currency", "entry_sequence", "held", "lock_token", "name", "normal_balance", "owner_id", "shard_index", "status", "system_code", "type", "updated_at" },
                values: new object[,]
                {
                    { new Guid("118019bb-214a-031e-c423-e1e022355c85"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Telco settlement (shard 2)", "Credit", null, 2, "Active", "TELCO_SETTLEMENT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("1f2f65d4-3123-ab51-bf07-e061d0ca9151"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Telco settlement (shard 0)", "Credit", null, 0, "Active", "TELCO_SETTLEMENT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("59cea765-836e-d1b3-3172-5b13b993809a"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Telco settlement (shard 1)", "Credit", null, 1, "Active", "TELCO_SETTLEMENT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("611e1599-2f29-3121-ca90-d2997129a1ff"), null, true, 0L, new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "PKR", 0L, 0L, 0L, "Telco settlement (shard 3)", "Credit", null, 3, "Active", "TELCO_SETTLEMENT", "System", new DateTimeOffset(new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("118019bb-214a-031e-c423-e1e022355c85"));

            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("1f2f65d4-3123-ab51-bf07-e061d0ca9151"));

            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("59cea765-836e-d1b3-3172-5b13b993809a"));

            migrationBuilder.DeleteData(
                table: "accounts",
                keyColumn: "id",
                keyValue: new Guid("611e1599-2f29-3121-ca90-d2997129a1ff"));
        }
    }
}
