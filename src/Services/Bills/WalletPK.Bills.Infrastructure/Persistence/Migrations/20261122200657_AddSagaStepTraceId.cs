using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WalletPK.Bills.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaStepTraceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trace_id",
                table: "saga_steps",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trace_id",
                table: "saga_steps");
        }
    }
}
